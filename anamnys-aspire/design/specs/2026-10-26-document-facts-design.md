# Document Reading and Declared Facts (F-08)

**Date:** 2026-10-26
**Status:** Approved for implementation
**Scope:** Backlog F-08. Clean external documents (F-07) are read (embedded PDF text, OCR otherwise),
and the local model extracts the facts the document declares — issuer, registration, issue date, ICD,
medication, conclusion — each with its page and exact sentence. The provider confirms, corrects or
discards each fact; only confirmed or corrected facts count. F-09 (medication timeline) and the
analyses consume them.

Done when (flow 07): an attached document yields a summary of declared facts for review; the
correction is kept next to what the machine read; confirmed facts enter the record.

## 1. Decisions (agreed with the user, 2026-10-26)

| Topic | Decision |
|---|---|
| Text | Embedded PDF text first (PdfPig); pages with fewer than ~20 useful characters are treated as scanned: rendered (PDFium) and OCR'd (Tesseract, Portuguese) in process. JPG/PNG go straight to Tesseract. Tesseract `por` data downloaded on first use and verified by SHA-256, like the other models. |
| Extraction | Local Qwen model with a GBNF grammar returning up to 12 `{kind, value, page, quote}`; each quote must exist literally on that page (F-03 locator), else the fact is dropped; format validation for ICD (`X00` / `X00.0`), dates and registrations (council acronym + number); invalid → dropped. |
| Confirmation | Facts count only after the provider confirms or corrects them; correction stored in `CorrectedValue` next to `Value`; discard removes it from review (soft). |
| Re-read | Replaces only facts still awaiting review; confirmed/corrected/discarded facts are kept. |
| Limits | Up to 20 pages per document read; extra pages flagged. Model shares the single model gate. |
| Priority (2026-10-26) | Interactive model use (the "Sugerir temas" dialog, anything a provider waits for) goes before background extraction. The gate (`LlmGate`) grants waiting interactive requests first; extraction runs at background priority in steps (one decode of at most 128 prompt tokens, or one sampled token) and yields the gate between steps, so a suggestion waits at most one step, not a whole extraction. |
| Deletion (2026-10-26) | Deleting an external document deletes its extracted facts (every status, confirmed included) and its excerpt references, in the same transaction as the soft delete; audited as `facts_deleted` with the document id and the count. |
| Restart (2026-10-26) | The extraction queue is in memory; at start, every document read (done, clean, not deleted) with no `facts_extracted` entry since its `ReadAt` is queued again. |

## 2. Data

Migration `design/migrations/2026-10-26-document-facts.sql` (idempotent), mirrored in
`anamnys-db-script.sql`:
- `ExternalDocuments` gains `"ReadStatus" text` (`pending|reading|done|failed`, null until scanned
  clean), `"ReadAttempts" int NOT NULL DEFAULT 0`, `"ReadAt" timestamptz`, `"PageCount" int`,
  `"PagesTruncated" boolean NOT NULL DEFAULT false`, `"ExtractAttempts" int NOT NULL DEFAULT 0` (fact
  extractions started since the last read; the startup sweep skips a document at 2 or more).
- New `ExternalDocumentPages` (`Id`, `ExternalDocumentId` FK cascade, `ProviderId`, `PageNumber`,
  `Text`, `FromOcr` bool, `CreatedAt`; unique (document, page)).
- `ExtractedFacts` gains `"ProviderId" uuid NOT NULL`, `"Status" text NOT NULL DEFAULT 'pending'`
  (`pending|confirmed|corrected|discarded`), `"Quote" text`, `"CreatedAt"`, `"DiscardedAt"`; keep
  `PageRef`, `ExcerptRefId`, `CorrectedValue`, `ConfirmedBy/At`; checks: corrected ⇒ CorrectedValue set.
- `ExcerptRefs` accepts `SourceType = 'document'` with `SourceId` = document id, `PageNumber` (new
  nullable int column) and offsets into that page's text; checks updated (note ⇒ SectionKey; document ⇒
  PageNumber).

## 3. Server

- `DocumentReading/`: `PdfTextReader` (PdfPig), `PageRenderer` (PDFium via a maintained .NET binding),
  `TesseractOcr` (in process, `por`, data file provider with SHA check like `LlmModelProvider`),
  `DocumentReader` (decides per page, limits), `ReadQueue` + `ReadWorker` (hosted; enqueued when the
  scan turns clean; retries; startup re-enqueue of `pending|reading`; logs exception type only).
- `DocumentFacts/`: `FactGrammar`, `FactPrompt` (pt-BR rules: only what the document states; never
  infer; copy the sentence verbatim; dates as written), `LlamaFactExtractor` (shares `LlmModelProvider`
  and the suggester's gate; own context sized to the text), `FactValidator` (ICD, date, registration),
  `FactService` (persist with document excerpt refs; confirm/correct/discard with audit; re-extract
  keeping reviewed facts; list per document; declared facts per patient — confirmed/corrected only).
- Endpoints (`/api/phi`, 401 first, provider-scoped, archived patient → 409 for mutations):
  `GET providers/me/external-documents/{id}/facts`;
  `POST …/external-documents/{id}/facts/{factId}/confirm` / `correct` `{ value }` / `discard`;
  `POST …/external-documents/{id}/extract` → 202;
  `GET providers/me/patients/{id}/declared-facts`.
  Existing `GET providers/me/excerpts/{id}` returns a document passage `{ kind, documentId, pageNumber,
  pageText, start, end, ... }` for `SourceType = 'document'`.
- External document list/search items gain `readStatus` and `pendingFacts` count.
- Audit actions: `fact_confirmed`, `fact_corrected`, `fact_discarded`, `facts_extracted`, `facts_deleted`
  (values: fact id and kind, or document id and a count — never fact values or page text).

### 3.1 Added during implementation (2026-10-26)

- **Shared gate:** `NoteStructuring/LlmGate` (singleton) replaces the suggester's own semaphore; priorities
  `Interactive` (codes-only suggest) and `Background` (with-quotes proposals, locate, warm-up, extraction).
  `YieldAsync` hands the gate to a waiting interactive request and takes it back (not cancellable, so the
  caller always holds it on return). Measured (Qwen2.5-7B Q4_K_M, CPU, 19 threads): suggest alone 4.0–4.3 s;
  suggest while an extraction runs 5.8–7.0 s; an extraction of a one-page laudo about 60–90 s.
- **Grammar:** GBNF rule names use letters, digits and `-` only — llama.cpp rejects `_`, and a grammar it
  cannot parse crashes the process (access violation) at the first sample.
- **Cited ICD:** the model tends to file "CID-10 F43.1 - …" as a conclusion only; a code written after
  "CID" in a candidate's quote also becomes an `icd` candidate (same page and quote). Nothing is inferred.
- **Registration:** the value is taken from the text, acronym to number ("Psiquiatra - CRM-SP 000000
  (fictício)" → "CRM-SP 000000").
- **Review rules:** confirm only from `pending` (confirming a confirmed fact is a no-op); correct from any
  status but `discarded`, the kind's format applies, and a correction equal to what was read confirms the
  fact; discard from any status. Re-extraction does not add a candidate equal (kind and value or corrected
  value) to a reviewed fact.
- **"Ler de novo"** (`POST …/extract`): read done → extract again; read failed (or never ran) → read again
  with attempts reset (the read then extracts); read pending/reading → nothing; not scanned clean → 409
  `not_available`.
- **Responses:** list/search items carry `readStatus`, `pendingFacts` and `extracting` (queued or running);
  `GET …/facts` returns `{ documentId, readStatus, pageCount, pagesTruncated, extracting, facts[] }`
  (discarded left out); declared facts carry `value` (the correction when corrected), `readValue` and
  `corrected`; the excerpt passage carries `sourceType` (`note` or `document`).
- **Configuration:** `DocumentFacts:Extractor` = `llama` (default) or `none` (no extraction; the test
  AppHost uses it so the model never loads in tests).

## 4. Provider app

- Document card/list: read status next to scan status ("lendo…", "N fatos a conferir", "lido").
- "Ver fatos" panel (modal or side panel): facts grouped by kind with value, page and quote; Confirmar,
  Corrigir (inline input; shows "lido: X → corrigido: Y"), Descartar, "Confirmar todos", "Ler de novo",
  link to open the original; "ver no documento" shows the page text with the sentence highlighted.
- Patient record: block "Fatos de documentos" listing confirmed/corrected facts (medications, ICDs,
  conclusions, issuers) with document date and kind, linking to the fact's passage.
- Copy in pt.json; polling while reading; read-only for archived patients.

## 5. Documentation

Chapter `documentation/27-document-facts.md`; environment chapter (Tesseract data download + hash,
PDFium native library, migration); index; status (F-08 done; Next F-09); gaps; Notion.

## 6. Testing

- Pure: page classification (text vs scanned), grammar, quote verification, validators, prompt.
- With real libraries (no model): embedded text from the dev-seed sample PDFs; OCR of an image
  rendered in the test (known Portuguese sentence) — skipped when the Tesseract data is not available.
- Service (real DB, fake extractor): read → facts with document refs; confirm/correct/discard + audit;
  re-extract keeps reviewed facts; scoping; archived 409; declared facts only confirmed/corrected;
  deleting a document removes its facts and references; the restart sweep.
- Gate (fakes): interactive before background, arrival order, yield between steps, cancellation.
- Real-model probe (manual): the 6 sample PDFs — timings and extracted facts reported in the docs; suggest
  latency while an extraction runs.
