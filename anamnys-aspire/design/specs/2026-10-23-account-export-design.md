# Full Account Export (F-60)

**Date:** 2026-10-23
**Status:** Approved for implementation
**Scope:** Backlog F-60 and decision D-08. One action produces everything the provider created, in an
open, documented format that opens and reads without Anamnys.

Done when (backlog): the provider exports the whole account and the file opens and is readable without
our software.

Out of scope (belongs to F-57, cancellation): the 60-day post-cancellation window and the deletion of
the account's data after it. Recorded in the gaps file under F-57. F-44 (per-patient dossier for a
council or court) is a different feature.

## 1. Decisions (agreed with the user, 2026-10-23)

| Topic | Decision |
|---|---|
| D-08 | Export available at any time while the account is active and for 60 days after cancellation; then the data is deleted. Site (pricing, FAQ, about) and Terms say exactly that. |
| Delivery | Export for active accounts now; post-cancellation window and deletion with F-57. |
| Format | ZIP with three layers: offline HTML to read, PDFs of signed notes and signed psychological documents, JSON + CSV for reuse, documented in a LEIA-ME. |
| Exclusions | No raw text at all: neither voice transcripts nor the original typed text (`Notes.RawTranscript`). Audit trail without previous/new values. |
| Inclusions | Note sections (signed and drafts marked "rascunho"), psychological documents (superseded marked), scales, diagnoses, medications, plan objectives, themes, appointments, patient registration data, archived patients too. |

## 2. Flow

- Ajustes → section "Exportar meus dados": "Gerar exportação"; one export in progress per provider
  (409 `export_in_progress`). Request writes an `AccountExports` row and an audit entry.
- An in-process queue (channel + hosted worker, like `TranscriptionQueue`/`TranscriptionWorker`) builds
  the ZIP from data scoped to the provider (every query filters by `ProviderId`, patients included).
  Existing PDFs are read from storage; missing ones (signed notes / signed documents never exported) are
  rendered with the existing `NotePdf`/`DocumentPdf` from the signing snapshot, without storing them.
- ZIP stored in the private bucket at `exports/{providerId}/{exportId}.zip`; download name
  `anamnys-exportacao-AAAA-MM-DD.zip` (no patient name).
- On completion: in-app notification (bell) "Sua exportação está pronta" (`Notifications`, channel
  `in_app`, kind `account_export_ready`, subject the export id); the section shows "Baixar" which
  returns a one-hour presigned link (new link on each click).
- Retention: the ZIP is deleted from storage 7 days after completion by a periodic sweep; the
  `AccountExports` row stays with `ObjectKey` cleared and `ExpiredAt` set.
- Failure: status `failed`, reason logged without patient data; the section offers "tentar de novo".
- Restart: an export left `running` when the server stops is marked `failed` at start (like
  `OrphanNoteSweep`).
- Screen warning: "O arquivo contém dados de saúde dos seus pacientes. Guarde-o em local seguro."

## 3. Data

Migration `design/migrations/2026-10-23-account-export.sql` (idempotent), mirrored in
`anamnys-db-script.sql`: `AccountExports` gains `"Status" text NOT NULL DEFAULT 'queued'`
(`queued|running|done|failed|expired`), `"FailureReason" text`, `"SizeBytes" bigint`,
`"ExpiredAt" timestamptz`; checks: done ⇔ `CompletedAt` set; `ObjectKey` only when done; FK to
`Providers`; partial unique index one `queued|running` export per provider.

Storage (`INoteFileStore`) gains `GetAsync(key, ct)` (bytes or null) and `DeleteAsync(key, ct)` for
reading existing PDFs and removing expired ZIPs.

## 4. ZIP layout

```
anamnys-exportacao-AAAA-MM-DD/
  index.html                 account summary + patient list (links to each patient page)
  LEIA-ME.txt                what each folder and file contains, field meanings, formats, encoding UTF-8
  conta/perfil.json          provider profile (name, CRP, city, settings that matter)
  conta/agenda.csv           all appointments (patient folder id, start/end, status, modality)
  conta/auditoria.csv        audit trail: date, action, field, note/document id — no values
  pacientes/NNN-XX/          NNN = 3-digit sequence by name, XX = initials (no full name in paths)
    paciente.html            readable record: registration data, diagnoses, medications, objectives,
                             scales, themes, notes (sections), documents (with links to the PDFs)
    paciente.json            same data, structured
    notas/AAAA-MM-DD-assinada[-N].pdf   signed notes
    notas/notas.json, notas/notas.csv   all notes: id, date, status, format, sections, themes
    documentos/AAAA-MM-DD-<tipo>[-N].pdf signed documents (superseded ones suffixed -substituido)
    documentos/documentos.json          all documents incl. drafts (fields, status, supersededBy)
    escalas.csv, diagnosticos.csv, medicacoes.csv, objetivos.csv, temas.csv
```

- HTML: static, inline CSS, no JavaScript, no external resources, `lang="pt-BR"`, readable when printed.
- CSV: UTF-8 with BOM (opens correctly in Excel), `;` separator, header row, ISO dates.
- JSON: UTF-8, documented field names in LEIA-ME, ISO-8601 dates.
- Never written anywhere in the ZIP: `Notes.RawTranscript`, live-session audio or transcripts, audit
  previous/new values, other providers' data.

## 5. API (`/api/phi`, provider-scoped, 401 first)

- `POST providers/me/exports` → 202 `{ id, status }`; 409 `export_in_progress`.
- `GET providers/me/exports` → latest export `{ id, status, requestedAt, completedAt, sizeBytes,
  expiresAt }` or 204 when none.
- `POST providers/me/exports/{id}/download` → `{ url, expiresAt }` (one-hour link); 404 unknown/other
  provider; 409 `not_ready` / `expired`.
- Audit: `account_export_requested`, `account_export_downloaded` (in `AuditEntries`, no note/document id;
  actor = provider).

## 6. Site and Terms (D-08)

Replace "o tempo todo"/equivalent promises in `apps/web/src/content/site/faq.md`,
`apps/web/src/content/legal/terms-of-service.md` and the pricing/about strings in `pt.json` with:
"Exporte todos os seus dados a qualquer momento enquanto sua conta estiver ativa e por 60 dias após o
cancelamento; depois disso, os dados são apagados." (adapted to each sentence, same meaning). The Terms
keep their structure; only the export clause changes.

## 7. Provider app

Settings section "Exportar meus dados": explanation of what is included and excluded (no transcripts),
the warning, button, state (queued/running with spinner and polling, done with size and "disponível até
<data>", failed with retry, expired with "gerar nova"), download button. Bell notification links to the
section.

## 8. Documentation

Chapter `documentation/24-account-export.md`; index; status (F-60 done); gaps (F-57 owns the 60-day
window and deletion; exported ZIP leaves our control); environment checklist (new migration); Notion.

## 9. Testing

- ZIP builder (real DB, scratch data): layout as §4; only the provider's patients; archived patients
  included; drafts marked; superseded documents marked; a marker string stored in `RawTranscript` and in
  audit values never appears in any entry of the ZIP (scan all bytes, PDFs text-extracted or skipped with
  a reason); missing PDFs rendered; HTML has no `<script` and no `http` resource references.
- Service/API: one in progress → 409; download link only when done; other provider 404; expiry sweep
  deletes the object and marks expired; restart marks running as failed; notification written.
- Site texts: no remaining "o tempo todo" export promise (grep test or checked in review).
- Migration: idempotent, bootstrap parity, single-in-progress index.
