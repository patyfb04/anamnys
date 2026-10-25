# Chapter 26: External Documents (F-07)

How a provider attaches documents the patient brought (reports, prescriptions, exams, referrals) to
the patient's record, how each file is scanned for viruses before it can be opened, and what is
stored where. Last checked against the code on 2026-10-25 (branch `feature/external-documents`).
Design: `design/specs/2026-10-25-external-documents-design.md`.

What a future change has to respect: a file can be opened only when it is `clean` (section 3), the
object key never carries patient data (section 5), and the file is patient data, so it is never
logged or written to a temp file (sections 4 and 8).

---

## 1. Purpose

Done when (backlog): a report can be attached to the record and retrieved securely. This is the
upload half only. OCR and declared facts (F-08) and the medication timeline (F-09) build on it later.

---

## 2. Flow

1. The provider uploads a file from the patient record ("Documentos recebidos" card) with a kind, an
   optional document date and an optional description.
2. The server validates it, stores the object in the private bucket and inserts the row as `pending`.
3. `ScanQueue` carries the job to `ScanWorker`, which reads the object back, scans it with ClamAV and
   sets `clean`, `infected` or `failed`.
4. Only a `clean` document can be opened: the server returns a one-hour presigned link and the
   browser opens it in a new tab.

The provider app polls every 3 seconds while any listed document is `pending`.

---

## 3. Scanning

- `IVirusScanner` has two implementations. `ClamAvScanner` talks to clamd over TCP (INSTREAM,
  chunked; reply `stream: OK`, `FOUND` or an error). `FakeVirusScanner` treats a marker (EICAR-like)
  as infected; the server registers it only when `ExternalDocuments:Scanner=fake` **and** the
  environment is Development (the test fixture sets it, and the AppHost then adds no container).
- The AppHost runs ClamAV as a container. Image, volume, health check, outbound access and the
  measured cost are in chapter 20, section 2.4b; they are not repeated here.
- **One scan at a time**, in a hosted service. The queue is in memory, so at startup the worker
  sweeps: it re-queues every `pending` document and finishes the object deletion of infected ones
  a previous process left behind.
- **Real scan errors** (clamd `ERROR`, scan timeout) count as attempts: 3 attempts, backoff 10 s then
  60 s, then the status becomes `failed`.
- **Transport failures** (cannot connect, connect timeout, connection lost; `ScannerUnavailableException`)
  do not count as attempts. A scan timeout after a successful connection is not one of them: it counts as an attempt. The document stays `pending` and is retried with a backoff of 10 s
  doubling up to 5 minutes, for as long as clamd is unreachable. This covers the first start (the
  signature download) and signature reloads.
- **Cap for a file that kills the connection.** If clamd accepts the connection but drops it while that
  file is being scanned, repeatedly, for 30 minutes in a row (`ScanWorker.MaxConnectionLostDuration`;
  a connect failure resets the clock), the document becomes `failed`, so one bad file cannot retry forever.
- Failures are logged by exception type only.

**Infected:** in one transaction the status is set to `infected` and an `external_document_blocked`
audit row is written, with the object key still kept. Then the object is deleted and the key set to
null. If the delete fails, the next startup sweep finishes it. The row stays as a record that the
upload happened and was blocked.

---

## 4. Types and limits

| Rule | Value |
|---|---|
| Types | PDF, JPG, PNG |
| Size | at most 20 MB, not empty |
| Check | magic bytes (`%PDF-`, `FF D8 FF`, PNG signature) by `FileTypeSniffer`; the extension must agree with the content |
| Kinds | `laudo`, `receita`, `exame`, `encaminhamento`, `relatorio_externo`, `outro` |
| Description | optional, at most 200 characters |
| Document date | optional, `yyyy-MM-dd` |
| Original file name | kept only in the database, for display (truncated to 200) |

The provider app repeats the type and size check before sending (`fileCheck.ts`); the server is the
authority.

**Request size.** The upload endpoint caps the request at 20 MB plus 1 MB of multipart overhead, and
installs a form reader that buffers the file **in memory** (no temp file on the server disk). An
oversized request (Kestrel 413, or the multipart reader's length-limit `InvalidDataException`) is mapped to 400 `too_large`.
The file is read once into an exactly sized array.

---

## 5. Storage keys

Private bucket (the same SeaweedFS store as the PDF exports), key `external/{providerId}/{documentId}`.
No patient name, no original file name. `INoteFileStore.PresignGetInline(key, contentType, validFor)`
builds the one-hour link with an inline disposition so the browser shows the file instead of
downloading it.

---

## 6. Open, delete, archived patients

- **Open** (`POST .../open`): only `clean`; any other state answers 409 `not_available`. The open is
  audited.
- **Delete** (`DELETE`): soft (`DeletedAt`), the object is removed from storage, audited.
- **Archived patient:** read-only. Upload and delete answer 409 `archived`; the patient card hides
  the actions. Listing and opening still work.
- The Recebidos tab (provider-wide list): each search item carries `patientArchived`; for an archived
  patient the row hides Excluir (read-only).
- Another provider's document or patient answers 404.

---

## 7. Data model

Migration `design/migrations/2026-10-25-external-documents.sql` (idempotent, mirrored in
`anamnys-db-script.sql`). `ExternalDocuments` columns used:

- `Kind` (not null, check on the list above), `DocumentDate`, `Description` (at most 200).
- `ScanStatus` (`pending|clean|infected|failed`), `ScannedAt`, `ScanAttempts` (real scan errors only).
- `ObjectKey`: nullable, with a check that it is null only when the document is infected or deleted;
  still unique when present.
- `DeletedAt` (soft delete).
- Foreign key `(PatientId, ProviderId)` to `Patients`, added if absent; index (`ProviderId`,
  `UploadedAt` DESC).
- `AuditEntries_subject_ck` is extended with the four actions below. Without that, audit rows with
  neither note nor document would be rejected. The latest migration owns the full list, so
  `2026-10-23-account-export.sql` rewrites the constraint only when it does not already contain them.

Entity: `Data/Entities/ExternalDocument.cs`.

---

## 8. API and security rules

All under `/api/phi/providers/me` (401 first, provider-scoped, no PHI in URLs):

| Request | Result |
|---|---|
| `POST patients/{id}/external-documents` (multipart `file`, `kind`, `documentDate?`, `description?`) | 201 `{id, scanStatus}`; 400 `unsupported_type`, `too_large`, `type_mismatch`, `empty`, `invalid_form`, or a validation problem for kind, date, description; 404; 409 `archived` |
| `GET patients/{id}/external-documents` | plain array (`patientName` is null) |
| `POST external-documents/search` `{kind?, patientId?, name?, page?}` | `{items, hasMore}`, 25 per page, items carry `patientName` and `patientArchived`; the name goes in the body |
| `POST external-documents/{id}/open` | `{url, expiresAt}` or 409 `not_available` |
| `DELETE external-documents/{id}` | 204; 409 `archived` |

Audit actions: `external_document_uploaded`, `_opened`, `_deleted`, `_blocked`. `ActorId` is the
provider, `FieldChanged` is `external_document`, `NewValue` is `"{id} {kind}"`. No file names or
patient names in audit values.

Rules: the file is never logged and never written to a temp file; object keys carry ids only;
scanner and store errors are logged by type; the open link expires in one hour.

---

## 9. Provider app

`apps/provider/src/components/externalDocuments/`: `ExternalDocumentsCard` (patient record, read-only
for archived), `ReceivedDocumentsList` (Documentos page, tab Recebidos, with kind filter, name
search and "Carregar mais"), `ExternalDocumentRow` (status badge: verificando, disponível,
bloqueado, falhou; open; delete with confirmation), `UploadExternalDocumentModal` (picker and drag
and drop). Opening uses a tab opened synchronously on the click and then pointed at the link, to
avoid the popup blocker. API client: `packages/shared/src/api/externalDocuments.ts` (upload timeout
180 s). Copy under `externalDocuments` in `pt.json`.

---

## 10. Measured cost

ClamAV 1.4.3 on the dev machine: idle RAM 969 MiB; signatures about 113 MB (108 MiB); image 342 MB; a 5 MB PDF scans
in 0.47 s and a 20 MB PDF in 1.77 s. Details and the first-start behaviour: chapter 20, section 2.4b.
The memory peak while signatures reload was not measured.

---

## 11. Limits, and what F-08 adds

- Scanning detects known malware only; it does not read the document.
- The uploaded file is held in server memory during the request (up to about 21 MB per upload).
- Signatures are fetched from the ClamAV mirror, so the container needs outbound internet.
- The F-60 account export does not include external documents yet.
- There is no text extraction. F-08 adds self-hosted OCR and declared facts (with page and issuer,
  confirmed by the provider) on top of `clean` documents, using the excerpt reference model of
  chapter 25 for their source.
