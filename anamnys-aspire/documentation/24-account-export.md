# Chapter 24: Full Account Export (F-60)

How a provider exports everything they created in Anamnys as one ZIP that opens without our
software, what the ZIP contains and leaves out, and what is still missing (the 60-day window after
cancellation and deletion belong to F-57). Last checked against the code on 2026-10-24 (branch
`feature/account-export`). Design: `design/specs/2026-10-23-account-export-design.md`.

The code is a queue, a ZIP writer and a sweep. What a future change has to respect is the list of
things that must never enter the file, and that the file leaves our control once downloaded.

---

## 1. Purpose and decision D-08

The site and the Terms promise that the provider can take their data with them. F-60 makes it true,
and it must exist before the first paying subscriber.

**D-08 (decided 2026-10-23): the export is available at any time while the account is active and
for 60 days after cancellation; then the data is deleted.** The site (pricing, FAQ, about), the
Terms and the privacy policy say exactly that. Only the first half is built here. The 60-day window
and the deletion belong to F-57 (cancellation, part of the subscription work F-50 to F-59, not
built yet; see `design/gaps/2026-10-03-patient-flows-gaps.md`, "Exportação").

Done when (backlog): the provider exports the whole account and the file opens and is readable
without our software.

---

## 2. Flow

1. Ajustes, section "Exportar meus dados" (`AccountExportSection.tsx`), button "Gerar exportação".
   The section lists what is included and excluded and warns that the file holds patients' health
   data.
2. `POST /api/phi/providers/me/exports` writes an `AccountExports` row (`queued`) and an
   `AuditEntries` row (`account_export_requested`) and enqueues the job. 202 `{ id, status }`.
   A second request while one is `queued` or `running` gets 409 `export_in_progress`.
3. `ExportWorker` (a hosted service reading `ExportQueue`, an in-memory channel, like
   `TranscriptionQueue`) takes one job at a time: row to `running`, `AccountExportBuilder` writes the
   ZIP to a temporary file (deleted on close), the file is uploaded to the private bucket at
   `exports/{providerId}/{exportId}.zip`.
4. `AccountExportService.CompleteAsync` marks the row `done` (`CompletedAt`, `ObjectKey`,
   `SizeBytes`) and writes the bell notification in one transaction: channel `in_app`, kind
   `account_export_ready`, subject the export id. The bell links to the settings section.
5. `GET providers/me/exports` returns the latest export (`id`, `status`, `requestedAt`,
   `completedAt`, `sizeBytes`, `expiresAt`) or 204; the section polls it while `queued` or `running`.
6. `POST providers/me/exports/{id}/download` returns `{ url, expiresAt }`, a presigned link valid one
   hour, a new one on each click, and writes `account_export_downloaded`. 404 for an unknown id or
   another provider's export; 409 `not_ready` or `expired`. The download name is
   `anamnys-exportacao-AAAA-MM-DD.zip` (no patient name).
7. Failure: `failed` with `FailureReason` (`export_error`, or `interrupted` after a restart), the
   uploaded object is deleted when it exists, and the section offers "tentar de novo".

All export endpoints sit under `/api/phi`, are provider-scoped and answer 401 first.

---

## 3. Data model

Migration `design/migrations/2026-10-23-account-export.sql` (idempotent, mirrored in
`anamnys-db-script.sql`) extends the existing `AccountExports` table:

- `Status` text, `queued|running|done|failed|expired`, default `queued`.
- `FailureReason` text, `SizeBytes` bigint, `ExpiredAt` timestamptz.
- Checks: `CompletedAt` is set if and only if the status is `done` or `expired`; `ObjectKey` only when `done`.
- Foreign key to `Providers`.
- Partial unique index: one `queued|running` export per provider. This makes the 409 race-safe;
  `RequestAsync` also catches the unique violation.

`INoteFileStore` gained `GetAsync(key, ct)` (bytes or null) to read existing PDFs and
`DeleteAsync(key, ct)` to remove expired ZIPs.

---

## 4. ZIP layout and formats

```
anamnys-exportacao-AAAA-MM-DD/
  index.html                 account summary + patient list (links to each patient page)
  LEIA-ME.txt                every folder, file and field; formats; what is not included
  conta/perfil.json          provider profile
  conta/agenda.csv           all appointments
  conta/auditoria.csv        audit trail: date, action, field, note/document id; no values
  pacientes/NNN-XX/          NNN = 3-digit sequence by name, XX = initials
    paciente.html            readable record
    paciente.json            same data, structured
    notas/AAAA-MM-DD-assinada[-N].pdf
    notas/notas.json, notas/notas.csv
    documentos/AAAA-MM-DD-<tipo>[-N][-substituido].pdf
    documentos/documentos.json
    escalas.csv, diagnosticos.csv, medicacoes.csv, objetivos.csv, temas.csv
    pontos-para-retomar.csv  follow-ups (F-14): description, created, origin note, resolved, resolving note, quote
```

- **Included:** note sections (signed notes, and drafts marked "rascunho"), psychological documents
  (superseded ones marked), scales, diagnoses, medications, plan objectives (with the linked theme), follow-ups, themes, appointments
  (with the cancellation reason: it is provider-created data), patient registration data, archived
  patients too.
- **HTML:** static, inline CSS, no JavaScript, no external resources, `lang="pt-BR"`, readable printed.
- **CSV:** UTF-8 with BOM (Excel), `;` separator, header row, ISO dates, yes/no as "sim"/"não".
- **JSON:** UTF-8, ISO-8601 dates, absent fields as `null`, field names documented in `LEIA-ME.txt`.
- **PDFs:** signed notes and signed documents. A stored PDF is copied; a missing one is rendered from the
  signing snapshot with `NotePdf` / `DocumentPdf` and not stored. If a signed document's PDF is neither
  stored nor renderable, its `pdf` field is `null` and the page shows "PDF indisponível"; the text stays in
  `documentos.json` and `paciente.html`.
- **Pending AI theme proposals** are exported but flagged (`proposto: true`; "(proposta da IA)" in the HTML).
- Folder names carry only the sequence and initials, never the full name.

`LEIA-ME.txt` (`ExportReadme.cs`) is the reference for readers of the export; keep it in step with the
builder when a field changes.

---

## 5. Exclusions, and why

- **Voice transcripts and live-session text** (`Notes.RawTranscript`) and **the original typed text** before
  structuring. Only the sections the provider reviewed are exported. The transcript is raw material, may hold
  more than the provider chose to record, and is not what they signed. Audio is never kept anyway.
- **Audit previous/new values.** The audit CSV has date, action, field and note/document id only; the
  values may be clinical text.
- **Other providers' data**, and scales removed by the provider.

`AccountData` (`ExportData.cs`) is the only source the builder reads, and it never loads
`RawTranscript` or audit values, so the exclusion does not depend on a filter further down. The builder
test stores a marker string in both and scans every byte of the ZIP (for PDFs it checks the model input,
since PDF content is compressed).

---

## 6. Safety

- **Provider scoping.** Every query filters by `ProviderId`, patients included; the download looks the export
  up by id and provider (another provider's id is a 404).
- **No PHI in paths or logs.** Paths use sequence and initials; the download name and the object key hold
  ids and a date only. The worker and the sweep log the export id and the exception type, never messages.
- **Formula-safe CSV.** A text cell starting with `=`, `+`, `-`, `@`, tab or carriage return gets a leading
  apostrophe (`ExportFormat.FormulaSafe`), so a spreadsheet cannot run it. `LEIA-ME.txt` says so.
- **HTML without scripts.** All text is HTML-encoded; no `<script`, no external resource (tested).

---

## 7. Retention

The ZIP is kept 7 days after completion (`AccountExportService.Retention`). `ExportSweep` deletes expired
objects hourly: the object first, then the row goes to `expired` with `ObjectKey` cleared and `ExpiredAt`
set. A failed delete leaves the row `done` and the next sweep retries. The download refuses an export past
its 7 days even before the sweep has run (409 `expired`). The row stays as a record. A new export can be
generated at any time.

At server start the same sweep marks exports left `queued` or `running` as `failed` (`interrupted`), as
`OrphanNoteSweep` does for notes.

---

## 8. Limits

- **The whole account's text is loaded into memory** (`ExportData.LoadAsync`) before the ZIP is written. A
  very large account costs memory in proportion. PDFs are streamed one at a time and the ZIP goes to a
  temporary file, not to memory.
- **Single instance.** The queue is in-process and the restart sweep fails every `queued` or `running`
  export, so with several server instances an instance starting would fail another one's job. Same
  assumption as `OrphanNoteSweep`. A shutdown also leaves a running export unfinished until the next start.
- **One export at a time** per provider, and one worker for all providers.
- **The downloaded ZIP leaves our control.** It holds patients' health data, is not encrypted and has no
  password; the screen only warns. Protecting it is the provider's job.
- There is no frontend automated test; the section is verified in the browser only.

---

## 9. What F-57 and F-56 must add

D-08 is only half implemented. Cancellation (F-57) must:

- keep the export available for 60 days after cancellation, and also for 60 days after a trial ends or an
  account is suspended (Terms 10.3 and the FAQ already promise it; today there is no such state, so only
  active accounts export);
- delete the account's data after the 60 days, including `exports/{providerId}/` in the bucket;
- settle the preservation clause for suspension in the Terms (it is still bracketed there).

F-44 (per-patient dossier for a council or court) is a different feature and is not covered.

---

## 10. Where things are

| Piece | File |
|---|---|
| Endpoints | `Exports/ExportEndpoints.cs` |
| Request, latest, download, complete, expire | `Exports/AccountExportService.cs` |
| Queue, worker, sweep | `Exports/ExportQueue.cs`, `ExportWorker.cs`, `ExportSweep.cs` |
| ZIP builder, data, views | `Exports/AccountExportBuilder.cs`, `ExportData.cs`, `ExportModels.cs` |
| Formats, HTML, LEIA-ME | `Exports/ExportFormat.cs`, `ExportHtml.cs`, `ExportReadme.cs` |
| Bell notification | `Notifications/ProviderNotifications.cs`, `NotificationBell.tsx` |
| UI | `apps/provider/src/components/settings/AccountExportSection.tsx` |
| Tests | `anamnys-aspire.Tests/Exports/`, `Data/AccountExportMigrationTests.cs` |
