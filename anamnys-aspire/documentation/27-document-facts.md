# Chapter 27: Document Reading and Declared Facts (F-08)

How a clean external document is read (embedded PDF text, OCR for scans), how the local model
proposes the facts the document declares, and how the provider confirms, corrects or discards them.
Last checked against the code on 2026-10-26 (branch `feature/document-facts`). Design:
`design/specs/2026-10-26-document-facts-design.md`.

What a future change has to respect: a fact counts only after the provider confirms or corrects it
(section 6), every fact keeps a literal quote that exists on its page (section 5), the model is shared
and interactive use goes first (section 7), and no fact value or page text is ever logged or audited
(section 10).

---

## 1. Purpose

Done when (flow 07): an attached document yields a summary of declared facts for review; the
correction is kept next to what the machine read; confirmed facts enter the record. F-09 (medication
timeline) and the analyses read the confirmed facts through `DeclaredForPatientAsync`.

Fact kinds: `issuer`, `registration` (council acronym and number), `issue_date`, `icd`, `medication`,
`conclusion`.

---

## 2. Flow

1. `ScanWorker` marks a document `clean` and, in the same update, sets `ReadStatus='pending'` and
   `ReadAttempts=0`, then enqueues a `ReadJob`.
2. `ReadWorker` (one read at a time) reads the document and stores its pages. On success it enqueues
   the document in `FactQueue`.
3. `FactWorker` (one extraction at a time) runs the model and stores the candidates as `pending` facts.
4. The provider reviews them in the "Ver fatos" modal: confirm, correct or discard.
5. Confirmed and corrected facts appear in the patient record and in `declared-facts`.

---

## 3. Reading (`DocumentReading/`)

- **Packages** (Server csproj): PdfPig 0.1.16 (Apache-2.0, embedded text), PDFtoImage 5.4.0 (MIT, with
  the bblanchon PDFium natives and SkiaSharp) and Tesseract.CrossPlatform 5.5.2.200 (Apache-2.0, with
  Tesseract.Native 5.5.2.200). Tesseract.CrossPlatform was chosen because the other bindings ship
  Windows DLLs only. It is young and has one maintainer; Linux loading is untested (chapter 20, 2.4c).
- **Per page** (`DocumentReader`): a PDF page with at least 20 useful characters (letters and digits)
  keeps its embedded text. Otherwise it is a scan: PDFium renders it (DPI capped so the longest side is
  at most 4200 px) and Tesseract (`por`, LSTM) reads it. The OCR text replaces the embedded text only if
  it has more useful characters. JPG and PNG go straight to OCR as page 1; images larger than 4200 px
  are scaled down first (`ImageScaler`).
- **Decompression cap:** `ImageScaler` reads only the image header first and refuses (as
  `UnreadableDocumentException`, so the read fails at once) any image whose width times height exceeds
  50 MP (`MaxDecodedPixels`), before any pixel buffer exists, on the scaling path and on the
  pass-through path to Tesseract (`Pix.LoadFromMemory`) alike. A large JPEG is decoded directly at the
  nearest smaller scale its codec supports (`GetScaledDimensions`), then resized.
- **Limit:** the first 20 pages. A longer document gets `PagesTruncated`.
- **Text** is normalised (`\n`, no control characters, blank lines collapsed) and stored one row per
  page in `ExternalDocumentPages` with `FromOcr`.
- **Tesseract data** (`TessdataProvider`): downloaded on first use and verified by SHA-256; see
  chapter 20, 2.4c. One OCR engine per process, one caller at a time.
- **Retries:** up to 3 attempts, waiting 30 s and then 2 min, then `failed`. The attempt is counted
  before the read starts, so a document that crashes the process still counts. At startup the sweep
  re-queues `pending` and `reading` documents and marks `failed` those whose attempts are used up
  (no crash loop). A missing object, an unsupported type, an encrypted or malformed PDF or an image
  that does not decode fails at once (`UnsupportedDocumentException`, `UnreadableDocumentException`).
- **Saving** runs in one transaction that takes `SELECT ... FOR UPDATE` on the document row, and only
  if it is still `reading` and not deleted. A delete running at the same time either wins (no pages
  stored) or runs after (pages removed).

---

## 4. Extraction (`DocumentFacts/`)

- `LlamaFactExtractor` uses the Qwen weights of `LlmModelProvider` (chapter 23) and the shared
  `LlmGate`. Each run has its own context, sized to the prompt plus 1200 output tokens, between 1024 and
  `NoteStructuring:ContextSize`. Pages are packed into runs that fit; a page that alone does not fit is
  cut to a prefix.
- **Prompt** (`FactPrompt`, pt-BR): only what the document states, never infer; copy the quote verbatim;
  dates as written; patient data is not a fact; a cited CID is an `icd`; an exam result is not a
  medication. Pages are given as `[Página N]`; control strings are stripped.
- **Grammar** (`FactGrammar`, GBNF): up to 12 items `{kind, page, quote, value}`, quote before value.
  `kind` is one of the six, `page` is one of the pages given, quote at most 200 characters, value at most
  160. An ICD value has the shape `X00` or `X00.0` in the grammar itself. **Rule names use `-`, never
  `_`:** llama.cpp rejects `_`, and a grammar it cannot parse crashes the process (access violation) at
  the first sample. A test guards the names.
- `FactValidator` per kind: ICD (upper-cased, dot added, "CID-10 F43.1" becomes `F43.1`); date
  (dd/mm/yyyy, dd/mm/yy, dd.mm, ISO, "14 de setembro de 2026"; must be a real day; kept as written);
  registration (acronym and 3 or more digits, taken from the text); other kinds: text with a letter, at
  most 200 characters. `WithCitedIcd` adds an `icd` candidate when a quote contains "CID ... X00.0",
  because the model tends to file that under `conclusion` only.
- **Config:** `DocumentFacts:Extractor` is `llama` (default) or `none` (no extraction). The AppHost
  forwards it and the test fixture sets `none`, so tests never load the model.

---

## 5. Storing the facts

`FactService.SaveExtractedAsync` runs in one transaction and first locks the document row
(`FOR UPDATE`), so an extraction finishing during a delete leaves nothing behind.

- It deletes the facts still `pending` and their document excerpt references.
- Each candidate is validated, and its quote is located on the stated page with the F-03 locator
  (`ExcerptService.FindInPage`). **A fact whose quote is not found on its page is dropped.**
- A candidate equal (kind and value or corrected value, case-insensitive) to a fact already reviewed is
  skipped, so a re-extraction does not bring back what the provider already decided.
- A kept fact gets an exact `document` excerpt reference (`PageNumber` and offsets into that page's
  text, chapter 25) and is stored as `pending` with its `Quote`.
- Audit `facts_extracted`, value `"{documentId} {count}"`.

Table `ExtractedFacts`: `ProviderId`, `Status` (`pending|confirmed|corrected|discarded`), `Value`,
`CorrectedValue`, `Quote`, `PageRef`, `ExcerptRefId`, `ConfirmedBy/At`, `CreatedAt`, `DiscardedAt`.
Checks: corrected needs `CorrectedValue`; confirmed or corrected needs `ConfirmedBy` and `ConfirmedAt`;
discarded needs `DiscardedAt`.

---

## 6. Review

| Action | Rule |
|---|---|
| Confirm | only from `pending`. Confirming a confirmed fact is a no-op. A corrected or discarded fact answers `not_pending` |
| Correct | from any status except `discarded`. The kind's format applies (else 400 on `value`). A correction equal to what was read confirms the fact instead |
| Discard | soft (`DiscardedAt`); the fact leaves the review list |

All three use a status-guarded update plus an audit row in one transaction (`fact_confirmed`,
`fact_corrected`, `fact_discarded`; value `"{factId} {kind}"`). The correction is stored next to the
original: `Value` is what the machine read, `CorrectedValue` what the provider wrote.

**Ler de novo** (`POST .../extract`):

| Read state | Effect |
|---|---|
| `done` | extraction runs again; reviewed facts stay, pending ones are replaced |
| `failed` or null | `ReadStatus='pending'`, attempts reset, read queued (the read then extracts) |
| `pending` or `reading` | nothing changes |
| document not scanned clean | 409 `not_available` |

Everything is provider-scoped (another provider's id answers 404). An archived patient answers 409
`archived` to every mutation.

---

## 7. The shared model gate

`NoteStructuring/LlmGate` (singleton) replaces the suggester's own semaphore; the theme suggester
(chapter 23) and the locate jobs (chapter 25) use it too.

- Two priorities. **Interactive:** the codes-only "Sugerir temas" call. **Background:** suggestions with
  quotes, locate, warm-up and fact extraction. On release, waiting interactive requests are granted first;
  within a priority, in arrival order. A cancelled waiter leaves the queue.
- Extraction calls `gate.YieldAsync()` before every decode step (at most 128 prompt tokens, or one sampled
  token with its forced JSON). If an interactive request is waiting, the gate goes to it and comes back;
  the extraction resumes in its own context. The wait to get the gate back cannot be cancelled, so the
  release is always balanced.
- **Measured** (Qwen2.5-7B Q4_K_M, CPU, 19 threads, one-page laudo): a suggestion takes 4.0 to 4.3 s alone
  and 5.8 to 7.0 s while an extraction runs (2 s, 20 s and 45 s into it). So a suggestion waits about one
  step, not the 60 to 90 s of an extraction.

---

## 8. Deleting and restarting

- **Deleting a document deletes its facts.** `ExternalDocumentService.DeleteAsync` runs one transaction:
  it updates the document row first (which locks it), removes the pages, then
  `FactService.DeleteForDocumentAsync` removes every fact of the document (all statuses, confirmed
  included) and its `document` excerpt references. Audit `facts_deleted`, value `"{documentId} {count}"`,
  only when there were facts.
- **Restart sweep:** the extraction queue is in memory. At startup `FactWorker.SweepAsync` queues every
  document that is `done`, clean, not deleted, with a `ReadAt` and no `facts_extracted` audit entry (same
  provider, value starting with the document id) at or after `ReadAt`. The read has its own sweep
  (section 3).
- **Extraction attempt guard (no crash loop):** `FactWorker.RunAsync` adds 1 to
  `ExternalDocuments.ExtractAttempts` before the model runs, so an extraction that brings the process
  down still counts. The sweep skips documents with 2 or more attempts since the read
  (`FactWorker.MaxSweepAttempts`) and logs how many it left out; their pending facts are left untouched.
  A new read (`ReadWorker`, also after a clean scan) and "Ler de novo" reset the count to 0.
- **Not covered:** a "Ler de novo" extraction interrupted after an earlier successful one. Its `ReadAt` is
  older than the earlier audit entry, so the sweep does not see it. Pressing the button again recovers it.

---

## 9. API

All under `/api/phi/providers/me` (401 first, provider-scoped, no PHI in URLs):

| Request | Result |
|---|---|
| `GET external-documents/{id}/facts` | `{documentId, readStatus, pageCount, pagesTruncated, extracting, facts[]}`; discarded facts left out, ordered by kind |
| `POST external-documents/{id}/facts/{factId}/confirm` / `discard` | 204; 404; 409 `not_pending` or `archived` |
| `POST external-documents/{id}/facts/{factId}/correct` `{value}` | 204; 400 validation problem on `value`; 404; 409 |
| `POST external-documents/{id}/extract` | 202 `{readStatus}`; 404; 409 `archived` or `not_available` |
| `GET patients/{id}/declared-facts` | confirmed and corrected facts of documents not deleted, or 404 |

Declared facts carry `value` (the correction when corrected), `readValue` and `corrected`; they are read
through `Excerpts.RequireExcerpt`, the rule of chapter 25, section 6. External document list and search
items gain `readStatus`, `pendingFacts` and `extracting` (queued or running, from `FactQueue.IsBusy`).

`GET excerpts/{id}` also serves document references: `{id, sourceType:"document", kind, documentId,
documentKind, patientId, datedAt, pageNumber, pageText, start, end}`. The range is marked exact only when
the text at the offsets still equals the quote. Note references now carry `sourceType:"note"`; the rest
of their shape is unchanged. After the document is deleted the passage answers 404.

---

## 10. Privacy rules

Logs carry the document id, counts, seconds and the exception type only. Audit values are ids, kinds and
counts: never a fact value, a quote or page text. Page text stays in the database; nothing is written to
disk except the Tesseract data file. Facts and pages of a deleted document are removed (section 8).

---

## 11. Provider app

`apps/provider/src/components/externalDocuments/`:

- `DocumentReadStatus`: badge "lendo...", "N fatos a conferir", "lido" or "leitura falhou" with
  "Ler de novo" (hidden when read-only) and "Ver fatos". Shown only when the scan is clean, on both the
  row and the card variants.
- `DocumentFactsModal`: facts grouped by kind with value, page and quote; Confirmar, Corrigir (inline
  input, shows "lido: X -> corrigido: Y"), Descartar, "Ver no documento"; footer with "Confirmar todos",
  "Ler de novo" and "Abrir original". It polls every 3 s while reading or extracting. Mutations are hidden
  when read-only (archived patient).
- `DocumentPassageModal`: loads the excerpt and shows the page text with the sentence highlighted.
- `DeclaredFactsCard`: block "Fatos de documentos" in the patient record, grouped by kind, with document
  kind and date, "lido: X" when corrected, and a link to the passage.
- API client `packages/shared/src/api/documentFacts.ts`; copy under `documentFacts` in `pt.json`.

---

## 12. Measured results and limits

Real-model probe on the six sample PDFs (Qwen2.5-7B Q4_K_M, CPU, embedded text, no OCR):

- **Time:** 47 to 90 s per document of one or two pages, warm, one extraction at a time. A batch of
  documents queues for minutes.
- **Quotes:** every kept quote was located on its page.
- **Recall is uneven.** Issuer, registration and date are often missed when they sit at the end of a short
  document. Some kinds are wrong (a therapy filed as `medication`) and exam results come out as paraphrased
  `conclusion` values (the quote is the literal line). The provider's review is the safeguard; nothing is
  inferred to fill the gaps.
- **Confirm all** sends one request per fact, in sequence (no bulk endpoint). A fact that answers
  `not_pending` (reviewed meanwhile) is skipped; any other error stops it.
- **No hang watchdog on the read path:** an OCR, PDF render or extraction call that never returns (without
  crashing) holds its worker until the process restarts; nothing times it out. Deferred.
- **Linux:** Tesseract loading is untested there (chapter 20, 2.4c).
- Not exercised in a browser; the fixture tests use a fake extractor.
- Only note sections and document pages are excerpt sources; the F-60 export does not include external
  documents or facts yet.

---

## 13. Where things are

| Piece | Path |
|---|---|
| Reading | `anamnys-aspire.Server/DocumentReading/` |
| Facts | `anamnys-aspire.Server/DocumentFacts/` |
| Shared gate | `anamnys-aspire.Server/NoteStructuring/LlmGate.cs` |
| Document passages | `anamnys-aspire.Server/Excerpts/` |
| Migration | `design/migrations/2026-10-26-document-facts.sql` |
| Tests | `anamnys-aspire.Tests/DocumentReading/`, `anamnys-aspire.Tests/DocumentFacts/` |
| Provider app | `apps/provider/src/components/externalDocuments/` |
