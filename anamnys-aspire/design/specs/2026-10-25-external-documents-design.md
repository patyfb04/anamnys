# External Documents (F-07)

**Date:** 2026-10-25
**Status:** Approved for implementation
**Scope:** Backlog F-07. The provider uploads documents brought by the patient (reports, prescriptions,
exams, referrals) to the patient's record; each file is scanned by a self-hosted antivirus before it can
be opened. F-08 (OCR + declared facts) and F-09 (medication timeline) build on it.

Done when (backlog): a report can be attached to the record and retrieved securely.

## 1. Decisions (agreed with the user, 2026-10-25)

| Topic | Decision |
|---|---|
| Antivirus | ClamAV as an Aspire-managed container (`clamav/clamav`), internal network only, signature volume, auto-updated signatures. On in development too (the user wants to measure its cost). Automated tests use a fake scanner (the fixture does not start ClamAV). |
| Metadata | Kind (`laudo`, `receita`, `exame`, `encaminhamento`, `relatorio_externo`, `outro`), optional document date, optional short description (≤ 200). Issuer, registration, ICD and medications come later from OCR (F-08). |
| Types and size | PDF, JPG, PNG; ≤ 20 MB; type checked by magic bytes and must agree with the extension. |
| Storage | Private bucket, key `external/{providerId}/{documentId}` — no patient name, no original file name; original file name kept only in the database for display. |
| Infected | Object deleted immediately; row kept as `infected` with date (record that the upload happened and was blocked). |
| Open | Only `clean` documents, one-hour presigned link with inline disposition, audited. |
| Delete | Soft delete (`DeletedAt`), object removed from storage, audited. Archived patient: read-only (no upload, no delete). |
| Measurement | After ClamAV runs locally, measure idle memory, reload peak, disk, and scan time for 5 MB and 20 MB PDFs; record in the environment chapter. |

## 2. Data

Migration `design/migrations/2026-10-25-external-documents.sql` (idempotent), mirrored in
`anamnys-db-script.sql`. `ExternalDocuments` gains `"Kind" text NOT NULL` (check list above),
`"DocumentDate" date`, `"Description" text` (≤ 200), `"ScannedAt" timestamptz`, `"ScanAttempts" int
NOT NULL DEFAULT 0`, `"DeletedAt" timestamptz`; FK `(PatientId, ProviderId)` → `Patients(Id,
ProviderId)` if not present; index (`ProviderId`, `UploadedAt` DESC). Existing `ScanStatus`
(`pending|clean|infected|failed`), `ObjectKey` (nullable after infected/deleted — make it nullable and
drop the unique constraint only if it blocks nulls; keep uniqueness for non-null keys).

## 3. Server

- `Documents`-independent area `ExternalDocuments/`:
  - `FileTypeSniffer` (pure): PDF `%PDF-`, JPEG `FF D8 FF`, PNG `89 50 4E 47 0D 0A 1A 0A`; extension must match; empty file refused.
  - `IVirusScanner` with `ClamAvScanner` (clamd INSTREAM over TCP, chunked, `stream: OK` / `FOUND` / error) and a `FakeVirusScanner` in tests (EICAR-like marker → infected).
  - `ExternalDocumentService`: upload (validate, store object, row `pending`, enqueue scan, audit), list per patient, search (provider-wide, kind filter, patient-name search in POST body, 25 per page, hasMore), open (clean only → presigned inline link, audit), delete (soft, remove object, audit).
  - `ScanQueue` + `ScanWorker` (hosted): reads the object from storage, scans, sets `clean` / `infected` (delete object) / retries up to 3 with backoff then `failed`; logs exception type only. Startup sweep re-enqueues `pending` rows.
- Endpoints under `/api/phi` (401 first, provider-scoped, archived → 409 `archived`):
  `POST providers/me/patients/{id}/external-documents` (multipart: file, kind, documentDate?, description?) → 201 `{ id, scanStatus }`; 400 `unsupported_type` / `too_large` / `type_mismatch` / `empty`;
  `GET providers/me/patients/{id}/external-documents`; `POST providers/me/external-documents/search`;
  `POST providers/me/external-documents/{id}/open` → `{ url, expiresAt }` | 409 `not_available`;
  `DELETE providers/me/external-documents/{id}` → 204.
- Request size limit for the upload endpoint set to 20 MB + multipart overhead (Kestrel/form options).
- Audit actions `external_document_uploaded|opened|deleted|blocked` (no file names in values; ids/kinds only).
- AppHost: ClamAV container resource with a data volume, health check (clamd PING/PONG or port), internal endpoint passed to the server as configuration (`ExternalDocuments:ClamAvHost`/`Port`); setting `ExternalDocuments:Scanner` = `clamav` | `fake` (fixture sets `fake` and does not add the container). The server starts even if ClamAV is still loading; jobs wait/retry.

## 4. Provider app

- Patient record: card "Documentos recebidos" (list: original name, kind, document date, status badge verificando/disponível/bloqueado/falhou, uploaded at; "Enviar documento" dialog with file input + drag & drop, kind select, date, description; client-side type/size pre-check; open on click when available; delete with confirmation; polling while any is pending; read-only for archived patients).
- Sidebar "Documentos" page: tabs "Emitidos" (existing list) and "Recebidos" (provider-wide list with kind filter and patient-name search in POST body, "Carregar mais").
- Copy in pt.json; hover classes as elsewhere; no PHI in URLs.

## 5. Documentation

Chapter `documentation/26-external-documents.md`; `documentation/20-environment-configuration.md` (ClamAV container, outbound access to the signature mirror, measured cost, migration); index; status (F-07 done; Next F-08); gaps; Notion.

## 6. Testing

- Pure: sniffer (each type, mismatch, empty, unknown), clamd response parsing, size limit.
- Service/API (real DB, fake scanner): upload → pending → clean; infected → object deleted and row blocked; scanner error → retries → failed; restart re-enqueues pending; open only when clean (409 otherwise); provider scoping (other provider 404); archived 409; delete soft + object removed; search filters and paging; audit rows; no original file name in object keys.
- Manual (documented): real ClamAV with the EICAR test file → blocked; measurement script.
