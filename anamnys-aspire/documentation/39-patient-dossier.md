# Chapter 39: Patient Dossier (F-44)

How a provider answers a CRP request, a court order or a patient's own request with a single export: a ZIP with the
patient's official record (signed notes, signed issued documents, external documents, disclosures, compliance,
retention and the integrity proof), built in the background, timestamped, downloadable for 7 days and backed by a
delivery receipt that outlives the file. Last checked against the code on 2026-11-04 (branch
`feature/patient-dossier`). Design: `design/specs/2026-11-04-patient-dossier-design.md`.

What a future change has to respect: the dossier holds the official record only (no drafts, raw transcripts, audit
values or other patients, section 8); no file name and no manifest entry carries the patient's name (section 5); the
ZIP is stamped by its own SHA-256 digest so a third party can verify the file they hold (section 9); a timestamp
authority outage never fails a dossier (section 9); the note typed for reason "other" never reaches the audit, a log
or the list (section 11); and the product does not call this a "cofre" (vault): the word stays out of every screen and
file until F-46 (backup) exists and a restore has been rehearsed.

---

## 1. Purpose and scope

Done when (backlog): a CRP request or a court order can be answered with a single export.

Out of scope (recorded in the gaps file): structured clinical data as CSV (scales, diagnoses, medication,
objectives); sending the dossier by e-mail; a request made by the patient from the patient portal.

---

## 2. Decisions

| Topic | Decision |
|---|---|
| Format | A ZIP with `dossie.pdf` (the document to file), the original files, `manifesto.json` with the SHA-256 of every file, and the integrity proof. |
| Content | The official record only, see section 8. |
| Period | Optional `from`/`to`; default is the whole history. The cover states the period requested. |
| Request | Button "Gerar dossiê" on the patient page, with a mandatory reason. Built in the background; a bell notice when ready. |
| Lifetime | The ZIP is downloadable for 7 days, then deleted. The `Dossiers` row (dates, period, reason, ZIP hash, manifest, token) stays, so what was delivered can be proven later. |
| Timestamp | The ZIP's SHA-256 is timestamped with the F-45 authority. No authority, no token, still a dossier. |
| Integrity problems | The dossier is generated anyway; the problem is on the cover and in the integrity section. |
| Archived patients | A dossier can be generated (requests arrive after care has ended). Discard deletes the dossiers (section 10). |

---

## 3. Data and migration

Migration `design/migrations/2026-11-04-patient-dossier.sql` (idempotent, one transaction, mirrored in
`anamnys-db-script.sql`, and appended to the chains of the other migration tests). **It must be applied to any
persistent environment before the new server version starts.** Without it the dossier endpoints answer 500, the daily
sweep logs a failure at start, and the notification bell query (which now checks `Dossiers.ProviderId`) fails. It turns
the old schema-only `Dossiers` table into:

| Column | Notes |
|---|---|
| `Id`, `ProviderId` (FK), `PatientId` (FK) | provider added; patient FK added if missing |
| `Number` | per provider, 1, 2, 3... (used in file names); unique with `ProviderId`; `>= 1` |
| `Status` | `queued`, `running`, `done`, `failed`, `expired` |
| `Reason`, `ReasonNote` | `crp`, `court`, `patient_request`, `other`; the note is required (1..200 characters) if and only if `other` |
| `PeriodFrom`, `PeriodTo` | optional dates; `PeriodFrom <= PeriodTo` when both exist |
| `RequestedAt`, `GeneratedAt`, `ExpiresAt`, `ExpiredAt` | `GeneratedAt` is set if and only if the status is `done` or `expired`; `ExpiresAt = GeneratedAt + 7 days` |
| `ObjectKey`, `SizeBytes`, `ZipSha256` | key `dossiers/{providerId}/{dossierId}.zip`, set only while `done`; hash in lower-case hex |
| `Manifest` | jsonb copy of `manifesto.json` |
| `AnchorToken`, `AnchoredAt` | RFC 3161 token (DER) for the ZIP digest and its `genTime`; null when no token |
| `IntegrityVerified` | true when every item verified `intact` at generation |
| `FailureReason` | `dossier_error` or `interrupted` |

Also: a partial unique index `Dossiers_Patient_active_key` allows one `queued|running` dossier per patient (this makes
the 409 race-safe), `Dossiers_PatientId_idx` is on `(PatientId, RequestedAt)`, and `AuditEntries_subject_ck` gains
`dossier_requested`, `dossier_downloaded` and `dossier_receipt_downloaded`. `Notifications` has no kind constraint, so
the kind `dossier_ready` needed no SQL. Legacy rows (none expected) are given their patient's provider, a number, reason
`other` with a fixed note, and `done` or `expired`.

---

## 4. Flow

1. **Request** (`DossierService.RequestAsync`). Validates the reason, the note and the period, creates the row
   (`queued`, next `Number`), writes the audit `dossier_requested` and enqueues the id. A note sent with a reason other
   than `other` is dropped; a note over 200 characters answers `reason_note_required`. The race with another request is
   settled by the active-index violation (`dossier_in_progress`); a number collision is retried up to 5 times. A
   discarded or foreign patient is a 404; an archived patient is accepted.
2. **Worker** (`DossierWorker`, hosted service, in-memory `DossierQueue`, one job at a time, on the account export
   pattern). Row to `running`; `DossierBuilder.BuildAsync` writes the ZIP to a temporary file deleted on close; SHA-256
   of that file; the authority stamps the digest and the token is verified (section 9); the file is uploaded to the
   private bucket; one transaction sets `done`, `GeneratedAt`, `ExpiresAt`, the hash, size, manifest, token and
   `IntegrityVerified`, and inserts the bell notice.
3. **`GeneratedAt` is the build moment**, the same instant printed on the cover and in `manifesto.json`, not the commit
   instant a few seconds later, so the three agree.
4. **Failure.** `failed` with `dossier_error`, and the uploaded object is deleted when one exists. If the row is no
   longer `running` at completion (for example, a discard), the object is deleted and nothing is written. On server
   shutdown the row is left `running` for the restart handling.
5. **Restart and sweep** (`DossierSweep`, hosted, at start and then daily): rows left `running` before the start become
   `failed` / `interrupted`, rows `queued` before the start are re-enqueued; `done` rows past `ExpiresAt` have the object
   deleted first, then become `expired` with `ObjectKey` null and `ExpiredAt` set. A failed delete keeps the row `done`
   and is retried. Downloads refuse past `ExpiresAt` in between, and the list reports such a row as `expired`.

---

## 5. ZIP layout

```
dossie.pdf
manifesto.json
LEIA-ME.txt
notas/AAAA-MM-DD-nota[-N].pdf
documentos/AAAA-MM-DD-<tipo>[-N][-substituido].pdf
externos/AAAA-MM-DD-documento-externo[-N].<ext>
divulgacoes/AAAA-MM-DD-termo[-N].<pdf|jpg|png|bin>
integridade/comprovante.pdf
integridade/carimbos/<sequencia>.tsr
```

- Dates are practice-local days (America/Sao_Paulo). A name taken twice gets `-N` before the extension (compared
  ignoring case).
- **Notes:** a stored PDF is copied; a missing one is rendered with `NotePdf` and not stored (the account export rule).
- **Documents:** every signed version in the period. A version replaced by a *signed* correction is named
  `-substituido` and marked "(substituído)" in the summary; a correction still in draft marks nothing. A stored PDF is
  copied, otherwise rendered with `DocumentPdf`; when neither works the summary says "arquivo não incluído: PDF
  indisponível".
- **External documents:** only clean files with an object in storage, copied byte for byte. **The original name never
  appears in a file name** (it may hold the patient's name): the entry is `documento-externo`, the extension comes from
  the stored name (sanitized) or the MIME type, and the original name is only inside `dossie.pdf`. Deleted documents are
  left out entirely. In the summary: "arquivo não incluído: bloqueado" (infected), "verificação falhou" (scan failed),
  "pendente de verificação" (not scanned yet), "indisponível no armazenamento" (clean but missing).
- **Disclosures:** the authorization term attached in F-17 is copied byte for byte, the extension taken from its
  content.
- **Integrity:** `comprovante.pdf` is the F-45 report restricted to this patient's seals; the `.tsr` files are the raw
  tokens of the F-45 anchors that cover those seals.
- **`manifesto.json`:** `v: 1`, `dossierId`, `number`, `provider` (name, CRP), `patientId`, `generatedAt` (practice-local
  ISO), `period`, `reason` (the code only, never the note), and `files`: path, SHA-256 and bytes of every entry except the
  manifest itself. No patient name.
- **Write order:** original files first, then `comprovante.pdf`, `dossie.pdf`, `LEIA-ME.txt` and `manifesto.json` last,
  so the summary matches what was really included and nothing is held in memory beyond one file.
- **`LEIA-ME.txt`:** what each file is; how to check files against the manifest (`jq ... | sha256sum -c`, with
  `shasum` and `Get-FileHash` equivalents); how to check the F-45 tokens (section 9); the disclosure period rule;
  what is not included and why.

The period rule: notes and documents by `SignedAt`, external documents by `UploadedAt`, both ends included, on
practice-local days. A disclosure authorization is included when its `AuthorizedAt`, a link's `CreatedAt`, `ExpiresAt`
or `RevokedAt`, or an access date falls in the period (a CRP request about a period must see the links used then); it
then comes with all its links and accesses.

---

## 6. `dossie.pdf` (QuestPDF, `DossierPdf`)

1. **Cover:** provider name and CRP; patient name and date of birth; generation date; period ("histórico completo"
   when none); reason, with the text for `other`; the integrity summary (all intact, or the problems found); the
   statement that the dossier was generated by Anamnys from the signed record; and that the ZIP hash and its timestamp
   are in the delivery receipt (a file cannot contain the hash of the ZIP it sits in).
2. **Chronological summary:** date, kind (nota, documento, documento externo, divulgação), title, file in the ZIP or the
   reason it is not included. The summary row of a disclosure is dated by its authorization even when a link or access
   pulled it into the period.
3. **Disclosures:** per authorization, recipient, role, purpose, date, how obtained, the term file; per link, created,
   expires, revoked or blocked, views against the cap, download; per access, date, outcome, user agent. Tokens and
   password hashes are never read.
4. **Compliance:** the latest F-05 check per signed document with the missing elements named by their catalog labels;
   "sem verificação registrada" when none.
5. **Retention:** last record date, retention date (F-06), active or archived, whether a disposal term exists.
6. **Integrity:** per note and document, the result of `IntegrityVerifier.VerifySubjectAsync` for that item at generation (`IntegrityVerified` is the aggregate), the seal sequence, sealed
   date, retroactive, anchored, and the anchors used.

An unsealed signed record counts as not verified (stricter than "every sealed item intact": a missing seal may mean a
deleted seal).

---

## 7. Delivery receipt (`DossierReceiptPdf`)

Available for `done` and `expired` dossiers, so it outlives the file. Content: number; provider and CRP; patient name;
requested and generated dates; reason (with the note for `other`) and period; `ZipSha256`; the timestamp ("dd/MM/yyyy
HH:mm:ss (horário de Brasília), emitido por <signer CN>", the authority's name being the CN of the token's signing
certificate, or a "sem carimbo" statement when there is no token); the ZIP file name, the `.tsr` file name and the exact
`openssl` command (section 9); the manifest's file list with bytes and hashes; and the status ("pronto para download até
DD/MM/AAAA" or "expirado em DD/MM/AAAA (o arquivo ZIP foi excluído; este registro é mantido)"). Download name `anamnys-dossie-NNN-comprovante.pdf`.

---

## 8. What is in and what is out

In: signed notes (sections as signed), signed issued documents (all versions), clean external documents, disclosures
with their links and accesses, the latest compliance check, retention and integrity.

Out, and tested with marker strings: drafts of notes and documents, the raw transcript, audit values, share-link token
and password hashes, another patient's data, another provider's data. Every ZIP entry is scanned raw (Latin-1 and
UTF-8); for PDFs the model input is checked.

---

## 9. Timestamp and how a third party verifies it

The worker takes the SHA-256 of the finished ZIP and asks the authority to stamp that **raw 32-byte digest**
(`ITimestampAuthority.StampDigestAsync`; `VerifyDigest` checks a token against it; both share `TimestampTokens.VerifyImprint`
with the F-45 verification). The F-45 anchors are different and unchanged: they stamp the SHA-256 of the head hash's hex
text (chapter 38). Because the dossier token stamps the file itself, a recipient can check the ZIP they hold:

```bash
openssl ts -verify -data anamnys-dossie-AAAA-MM-DD-NNN.zip -in anamnys-dossie-NNN.tsr -token_in -CAfile raiz.pem
```

`-token_in` is required: the `.tsr` is a bare DER TimeStampToken, not a full TimeStampResp. Expected answer: `Verification: OK`. `raiz.pem` is the authority's root certificate (FreeTSA publishes its own); the receipt
shows the signer's name. The token is obtained by the provider with `GET /api/phi/dossiers/{id}/timestamp.tsr` (section
12), also after the ZIP has expired, and handed over together with the receipt. The tokens inside the ZIP
(`integridade/carimbos/`) are the F-45 anchors and are checked as chapter 38 section 12 describes (the LEIA-ME gives
each seal hash and its stamped digest).

If the authority fails (network, invalid response, or a token that does not verify), the dossier is still `done`, with no
token and a warning logged with the dossier id and the exception type only; the receipt says "sem carimbo".

---

## 10. Discard

`DiscardService` already deleted the patient's dossier objects from storage and its `Dossiers` rows. It now also deletes
the `dossier` notices of those dossiers (before the rows, so the notice subquery still finds them), so the bell
count does not keep an unreadable notice. A build finishing after the discard finds the row gone or not `running`, and
deletes its object.

---

## 11. Audit and privacy

- `dossier_requested`: `"{dossierId} {reason} {from|-}..{to|-}"`. `dossier_downloaded`: the dossier id.
  `dossier_receipt_downloaded`: the dossier id for the PDF, `"<id> tsr"` for the token. Field changed `dossier`.
- The note for reason `other` is stored on the row and printed in `dossie.pdf` and the receipt. It is never audited,
  logged or listed (the list has no `reasonNote`), and the manifest holds the reason code only.
- Download names carry no patient name. The download link is a presigned URL valid for one hour or until `ExpiresAt`,
  whichever is earlier, new on each click, and the presign happens before the audit entry.
- The bell notice `dossier_ready` is shown to its own provider only, with the patient's name and id.

---

## 12. Endpoints

In the PHI group, cookie session, 401 first, unknown, foreign or discarded ids answer 404, every answer is
`Cache-Control: no-store`.

| Method and path | Success | Errors |
|---|---|---|
| `POST /api/phi/patients/{patientId}/dossiers` body `{ reason, reasonNote?, periodFrom?, periodTo? }` | 202 `{ id, status }` | 400 `invalid_reason`, `reason_note_required`, `invalid_period`; 404; 409 `dossier_in_progress` |
| `GET /api/phi/patients/{patientId}/dossiers?page=n` | 200 `{ items, total, page, pageSize }`, 5 per page, newest first; each item `id, number, status, reason, periodFrom, periodTo, requestedAt, generatedAt, expiresAt, sizeBytes, anchored, integrityVerified` | 404 |
| `POST /api/phi/dossiers/{id}/download` | 200 `{ url, expiresAt }`, name `anamnys-dossie-AAAA-MM-DD-NNN.zip` | 404; 409 `not_ready`, `expired` |
| `GET /api/phi/dossiers/{id}/receipt.pdf` | 200 PDF, attachment `anamnys-dossie-NNN-comprovante.pdf` | 404; 409 `not_ready` |
| `GET /api/phi/dossiers/{id}/timestamp.tsr` | 200 raw DER `application/timestamp-reply`, attachment `anamnys-dossie-NNN.tsr` | 404 (also when generated without a token); 409 `not_ready` |

`integrityVerified` is null until the dossier is generated; the list may report `anchored` false while queued.

---

## 13. Provider UI

- **Card "Dossiês"** (`DossiersCard`, collapsible id `dossiers`) on the patient page, after the declared facts; read
  only for an archived patient. Server-paged list (own previous/next pager, 5 rows): date, period, reason, status ("Na
  fila", "Gerando", "Pronto até DD/MM/AAAA", "Falhou", "Expirado"); "Baixar" while ready (opens the presigned URL),
  "Comprovante de entrega" for ready and expired, "Carimbo de tempo (.tsr)" when there is a token, "Tentar de novo" on a
  failed row. Polls every 5 s while a row is queued or running.
- **Dialog** (`GenerateDossierModal`): period from/to (optional), reason radio ("Pedido do CRP", "Intimação ou ordem
  judicial", "Solicitação do próprio paciente", "Outro" with a text of up to 200), the warning "O arquivo contém dados de
  saúde do paciente. Entregue apenas a quem tem direito.", button "Gerar". The server codes map to messages; 409 reads
  "Já existe um dossiê sendo gerado para este paciente."
- **Retry** opens the dialog prefilled with the same reason and period. The server never lists the note, so for reason
  "other" the provider types it again.
- **Bell:** `dossier_ready` links to `/patients/$patientId#dossiers`, which opens and scrolls to the card.
- Shared: `packages/shared/src/api/dossiers.ts`, types in `lib/types.ts` (`NotificationItem.patientId`), copy under
  `dossier.*` and `notifications.dossierReady` in `pt.json`.

---

## 14. Tests

Under `anamnys-aspire.Tests/Dossiers/` and `Data/PatientDossierMigrationTests.cs`, on scratch databases, with the fake
authority and a fake object store:

- `DossierBuilderTests`: exact entry list for a period (time-zone boundary at 31/10 23:00), no period, external file and
  term byte for byte, blocked / failed / pending rows, the disclosure period rule, a draft correction not marking the
  original, the marker scan (drafts, transcript, audit values, hashes, other patient and provider, no patient name in any
  entry name or the manifest), manifest hashes, model contents, a tampered note giving `IntegrityVerified` false on the
  cover, file name sanitizing.
- `DossierFlowTests`: validation, 409, numbering, audit without the note, the worker (hash, token imprint equals the raw ZIP
  digest and the hex-text check fails, upload, completion, bell notice), TSA down, tampered note, store down, failure
  after upload, a row no longer running, restart recovery, sweep (and a failing delete), receipt after expiry, download
  link name, TTL and cap at `ExpiresAt`, token endpoint, list paging, discard.
- `DossierEndpointTests`: 401 over HTTP; handlers for codes, shapes, `no-store`, headers, 404 and 409 paths.
- `PatientDossierMigrationTests`: previous chain run twice equals the bootstrap, legacy rows, every constraint.

Runs: `*Dossier*` 8/8 (builder) and, with `*MigrationTests*`, 212/212 before the review fixes; `*Dossier*`, `*Discard*`
and `*Notification*` 169 total after them, 5 failing only because the dev database lacked the migration. Provider and
patient build and lint clean. The UI was not exercised in a browser and has no automated test.

---

## 15. Deployment

- Apply `2026-11-04-patient-dossier.sql` to every persistent environment before the new version starts (re-runnable).
- No new configuration: the dossier uses the F-45 authority (`Integrity:TsaUrl`, `Integrity:TsaRootCertificate`) and the
  existing private bucket and queue pattern. Without a configured root the dossier is built without a token.
- The ZIP is built in a temporary file, so the server needs free disk for the largest dossier (external files are
  limited to 20 MB each). Leftover files `anamnys-dossier-*.zip` are cleaned at start.
- See chapter 20.

---

## 16. Limits and known gaps

- Structured clinical data (scales, diagnoses, medication, objectives) is not in the dossier as CSV.
- No e-mail delivery: the provider downloads and hands it over.
- No request from the patient portal.
- The race paths (two requests at once, a discard during a build) have no automated test of their own.
- A row left `running` or `queued` after a database error during the build blocks new requests for that patient until the
  server restarts (the restart handling fails or re-enqueues it).
- Each external file is read into memory while copied (about twice its size, so roughly 40 MB at the 20 MB limit).
- A disclosure summary row is dated by its authorization even when a link or access pulled it into the period.
- Provider UI minors: no `keepPreviousData` on the pager (flicker), the page is not reset to 1 after a request, a second
  bell click on `#dossiers` does not re-trigger, a `dossier_ready` notice without a patient id falls back to the calendar
  link, no pending state on the receipt and token buttons.
- The token and the receipt are separate downloads; the timestamp is not embedded anywhere in the ZIP.

---

## 17. Where things are

| Piece | Path |
|---|---|
| Migration | `design/migrations/2026-11-04-patient-dossier.sql` |
| Entity | `anamnys-aspire.Server/Data/Entities/Dossier.cs` |
| Server code | `anamnys-aspire.Server/Dossiers/` (`DossierBuilder`, `DossierData`, `DossierPdf`, `DossierReadme`, `DossierFileNames`, `DossierService`, `DossierWorker` with `DossierSweep`, `DossierQueue`, `DossierEndpoints`, `DossierReceiptPdf`) |
| Stamping | `anamnys-aspire.Server/Integrity/ITimestampAuthority.cs`, `HttpTimestampAuthority.cs` (`TimestampTokens` is in `ITimestampAuthority.cs`) |
| Patient-scoped integrity report | `anamnys-aspire.Server/Integrity/IntegrityReportPdf.cs` |
| Discard and bell | `Retention/DiscardService.cs`, `Notifications/ProviderNotifications.cs` |
| Provider UI | `apps/provider/src/components/dossiers/` |
| API client | `packages/shared/src/api/dossiers.ts` |
| Tests | `anamnys-aspire.Tests/Dossiers/`, `anamnys-aspire.Tests/Data/PatientDossierMigrationTests.cs` |
