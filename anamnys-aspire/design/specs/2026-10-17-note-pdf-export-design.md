# Note PDF Export

**Date:** 2026-10-17
**Status:** Implemented
**Scope:** Closes Phase 1: a signed note can be exported as a PDF. The PDF is generated once, on the
first export, stored in a private self-hosted object store (SeaweedFS, S3-compatible), and handed to
the browser only through one-hour presigned URLs. Builds on
`2026-10-13-note-review-and-signing-design.md` and `2026-10-15-note-editor-design.md`.

Out of scope: ICP-Brasil digital signature inside the PDF, sealing the PDF into the `DocumentHashes`
chain (F-45), whole-account export (F-60), clinical documents (`ClinicalDocuments`, F-05).

---

## 1. Context

- CLAUDE.md (hard requirement): exported PDFs are ePHI, live in a private bucket, and are reached
  only through one-hour presigned URLs. Every dependency that touches PHI is self-hosted.
- `Notes."Status"` already allows `'Exported'`; `Notes_Signed_ck` requires `SignedAt` for
  `Signed`/`Exported`. `Notes` has no column for a stored file. `Dossiers."ObjectKey"` shows the
  intended pattern.
- A signed note never changes (`NoteEditing` refuses every mutation once `SignedAt` is set).
- The AppHost runs Redis, Postgres, Keycloak and (dev) Mailpit; there is no object store yet.
- The public FAQ says "Os documentos oficiais são PDFs assinados por você" and that they do not
  depend on the platform to stay valid. The transcript is never exported (FAQ, backlog F-28).
- Future items that will reuse the object store: F-07 (document upload), F-44 (dossier), F-60
  (account export).

## 2. Decisions

| Topic | Decision |
|---|---|
| When the PDF exists | Generated on the first "Exportar PDF", stored, and reused for every later export. The note becomes `Exported`; the file never changes. |
| Object store | SeaweedFS (Apache-2.0) in S3 mode, as an Aspire container with a persistent volume and volume-data encryption on. MinIO (AGPL, no community images since 2025) and Garage (AGPL, no Aspire integration) were rejected; Postgres storage was rejected because it contradicts the private-bucket rule and does not fit F-07/F-60 files. |
| Delivery | The API returns a presigned S3 GET URL valid for one hour; the browser downloads directly from the store. The store's S3 endpoint is reachable by the browser (dev: localhost port; production: its own TLS host), signature-only reads, no listing. |
| PDF library | QuestPDF, Community license (free while annual revenue is under US$ 1M; a paid licence is required above that). |
| Signature | Electronic signature recorded in Anamnys (name, CRP, date and time). No ICP-Brasil certificate. The FAQ wording is aligned to this. |
| PHI in URLs | The object key and the download file name contain only ids and a short note code, never a patient name. |

## 3. Data

Migration `design/migrations/2026-10-17-note-pdf.sql` (idempotent, all-or-nothing, run after
`2026-10-13-note-review.sql`):

- `Notes` gains `"PdfObjectKey" text`, `"PdfSha256" text`, `"ExportedAt" timestamptz`.
- `Notes_Exported_ck`: `("Status" = 'Exported') = ("PdfObjectKey" IS NOT NULL)`, and
  `"PdfObjectKey"`, `"PdfSha256"`, `"ExportedAt"` are all null or all set.
- Existing `Exported` rows (seed data only) are moved back to `Signed` before the constraint is
  added.
- `anamnys-db-script.sql` gets the same columns and constraint.

Object key: `notes/{providerId}/{noteId}.pdf`. Content type `application/pdf`.

## 4. Infrastructure

- `AppHost.cs`: container `storage`, image `chrislusf/seaweedfs` pinned to an exact release tag
  (never `latest`), command `server -s3` with the S3 config file holding one identity, a data
  volume, and volume-data encryption enabled. The S3 HTTP endpoint is exposed.
- Parameters `storage-access-key` and `storage-secret-key`, both `secret: true`, set per developer
  in user secrets like the Keycloak client secrets. CLAUDE.md "First-time setup" lists them.
- The server receives `Storage:ServiceUrl` (internal endpoint, used for uploads),
  `Storage:PublicUrl` (browser-facing endpoint, used only to sign URLs; SigV4 signs the host, so
  the URL must be signed for the host the browser will call), `Storage:AccessKey`,
  `Storage:SecretKey`, `Storage:Bucket` (`notes`).
- On startup the server creates the private bucket if it is missing (idempotent).
- Server package: `AWSSDK.S3` (path-style addressing).

## 5. Server

### 5.1 `INoteFileStore`

```csharp
public interface INoteFileStore
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);
    Uri PresignGet(string key, string downloadFileName, TimeSpan validFor);
}
```

`S3NoteFileStore` implements it with two `AmazonS3Client`s (service URL for `PutObject`, public URL
for `GetPreSignedURL`, with `ResponseHeaderOverrides.ContentDisposition =
attachment; filename="..."`). Tests use a fake.

### 5.2 `NotePdf` (document)

- `NotePdfModel` record: provider name, CRP (`"{number}/{region}"`), patient full name, patient
  birth date (nullable), session date (session `ScheduledStart` or `ActualStart`, else note
  `CreatedAt`), format, sections (key label + content, in format order), signer name, signed at,
  short note code (first 8 hex characters of the note id, upper case).
- `NotePdfModel.From(...)` builds the model from the note, its sections, the patient, the provider
  and the session. It never reads `RawTranscript`.
- `NotePdf.Render(NotePdfModel) : byte[]` (QuestPDF):
  - Header: provider name and "CRP {crp}".
  - Identification: patient name, "Nascimento: {date}" when present, "Atendimento: {date}",
    format.
  - Body: each section title with its content; an empty section shows "Nada registrado".
  - Signature block: "Assinado eletronicamente no Anamnys por {name}, CRP {crp}, em {date time}".
  - Footer: "Página X de Y" and "Nota {code}".
  - Dates in `America/Sao_Paulo`, pt-BR format.

### 5.3 `NoteExport.ExportAsync`

`ExportAsync(db, store, providerId, noteId, now, ct) : Task<ExportResult>` where
`ExportResult(ExportOutcome Outcome, Uri? Url, DateTimeOffset? ExpiresAt)` and
`ExportOutcome { Ok, NotFound, NotSigned, StorageUnavailable }`.

1. Transaction with `NoteLocks.LockAsync`; load the note scoped to the provider (404 otherwise).
2. `Status` not `Signed`/`Exported` → `NotSigned`.
3. If `PdfObjectKey` is null: build the model, render, compute SHA-256, `store.PutAsync` (a storage
   exception → `StorageUnavailable`, transaction rolled back), then set `PdfObjectKey`,
   `PdfSha256`, `ExportedAt = now`, `Status = 'Exported'`, `UpdatedAt`, and audit `exported`
   (`NewValue` = the SHA-256).
4. Otherwise audit `pdf_link_issued`.
5. Commit, then `store.PresignGet(key, $"nota-{code}.pdf", 1 h)`; `ExpiresAt = now + 1 h`.

A transaction that fails after the upload leaves an object at the same deterministic key; the next
attempt overwrites it.

### 5.4 Route

`POST /api/phi/providers/me/notes/{noteId}/export` → `200 { url, expiresAt }` | 404 |
`409 { message: "Assine a nota antes de exportar.", code: "not_signed" }` |
`503 { message: "Armazenamento indisponível. Tente de novo em instantes.", code: "storage_unavailable" }`.
`anamnys-aspire.Server.http` gains the request.

## 6. Provider app

- `SignCard` (note page): a signed note shows **"Exportar PDF"**; an exported note shows
  "Exportada em {date}" and **"Baixar PDF"**. Both call `notesApi.exportPdf(noteId)` and navigate
  the browser to the returned URL (download).
- Errors: 409/503 toast the server message; anything else a generic toast.
- `NoteDetail` gains `exportedAt`. The note list already labels `Exported`.
- `notesApi.export` (legacy, dead route) is replaced by `exportPdf`.

## 7. Documentation

- FAQ (`apps/web/src/content/site/faq.md`): the official documents are PDFs **signed
  electronically in Anamnys** (name, CRP, date and time) that the provider keeps and archives where
  they want; drop the claim that they stay valid independently of the platform; no mention of a
  digital certificate.
- CLAUDE.md: SeaweedFS in "Backend — present"; the two storage secrets in "First-time setup".
- Gaps file: ICP-Brasil signature (future); production storage host with TLS; backup of the
  SeaweedFS volume; QuestPDF revenue condition; PDF not yet sealed into `DocumentHashes` (F-45).
- Status file: "Exportação em PDF" done.

## 8. Failure handling

| Case | Result |
|---|---|
| Note of another provider, or missing | 404 |
| Note not signed | 409 `not_signed` |
| Upload fails | 503 `storage_unavailable`; nothing written to the database |
| Commit fails after upload | Error; object left at the deterministic key, overwritten on retry |
| URL used after one hour | The store refuses it (signature expired); the provider clicks again |

## 9. Testing

Automated (`dotnet run --project anamnys-aspire.Tests`):

- `NotePdfModel.From`: section order, "Nada registrado" for empty sections, signature data, session
  date fallback, transcript and original text never included.
- `NotePdf.Render`: output starts with `%PDF`.
- `NoteExport` with a fake store: first export uploads once and sets key, hash, `ExportedAt`,
  `Exported`, audit `exported`; second export does not upload and audits `pdf_link_issued`; not
  signed → `NotSigned`; another provider → `NotFound`; store throwing → `StorageUnavailable` and the
  note unchanged.
- Integration with the real SeaweedFS started by the test AppHost: export, then an HTTP GET on the
  presigned URL returns 200 `application/pdf` whose SHA-256 equals `PdfSha256`.
- Migration test: constraint rejects `Exported` without `PdfObjectKey`.
- Endpoint: 401 without a session.

Manual: export, download and open the PDF; download again (same file, new link).
