# Patient Quotes Timeline (F-15)

**Date:** 2026-10-30
**Status:** Approved for implementation
**Scope:** Backlog F-15. The provider marks a passage of a note as a patient quote, optionally with a theme; the
"Análises" page lists the recorded quotes chronologically, with date and origin.

Done when (backlog): quotes from different sessions appear side by side, with dates.

## 1. Decisions (agreed with the user, 2026-10-30)

| Topic | Decision |
|---|---|
| What is a quote | Only what the provider explicitly marks: a selected passage of a note in review or signed, optional active theme. Never inferred, by rule or by the model. The text is the selected passage, not editable. |
| Where | Card "Falas do paciente que você registrou" on "Análises", below "Temas por sessão": theme filter (default all), page period, cards side by side (1 to 3 columns), 12 per page, fixed sentence "Estas são as falas do paciente que você registrou nas notas." The grid's cell detail also lists that theme's quotes in that session. |
| Which appear | The list shows quotes of signed notes only, with the F-10 session date rule, chronological. The note view lists its own quotes, also in review. Filtering by a parent theme includes its children. |
| Language | Always "que você registrou"; never "as palavras do paciente", which would suggest the system listened (README §4d). No interpretation, no colours. |

## 2. Data

Migration `design/migrations/2026-10-30-patient-quotes.sql` (idempotent, mirrored in `anamnys-db-script.sql`,
appended to every migration test chain after `2026-10-30-plan-adherence.sql`):

- `PatientQuotes` gains `"AddedById" uuid`, `"RemovedAt" timestamptz`, `"RemovedById" uuid` with a check that the
  two removal columns go together, and an index on (`NoteId`) already exists.
- Audit actions `quote_recorded`, `quote_removed` added to `AuditEntries_subject_ck` with the same guard pattern.
- `DiscardService` already deletes quotes.

## 3. Server

- `POST providers/me/notes/{noteId}/quotes { sectionKey, offsetStart, offsetEnd, quote, termId? }`: 201 with the
  quote; 409 `note_not_ready` (not ReadyForReview/Signed/Exported), 409 `archived`, 400 `invalid_selection`, 400
  `term_inactive`-style error for an inactive term, 404 for another provider's or a discarded patient. Passage via the
  existing excerpt service (exact reference).
- `DELETE providers/me/quotes/{id}`: marks it removed; audited; repeat is 204.
- `GET providers/me/notes/{noteId}/quotes`: the note's active quotes.
- `GET providers/me/patients/{id}/quotes?from=&to=&termId=&page=` → `{ items: [{ id, text, on, noteId, excerptId,
  section, term?: { termId, label } }], total, page, pageSize: 12 }`; signed notes only, chronological; `termId`
  includes children. 404 for another provider's or a discarded patient.
- Export (F-60): per-patient `falas-registradas.csv` and `paciente.json`; removed quotes left out.

## 4. Provider app

- Note view: "Marcar como fala do paciente" in the selection actions (theme select optional, reuse `TermSelect`);
  list of the note's quotes with "Remover".
- "Análises": the card from §1 (reuse the paging style of the trajectory event list).
- Grid cell detail (`OccurrenceModal`): that term's quotes in that session.
- Copy in pt.json; hover classes; no PHI in URLs; archived read-only.

## 5. Dev seed

`dev-seed/analysis-samples.sql`: quotes on seeded notes.

## 6. Documentation

Chapter `documentation/35-patient-quotes.md`; index; `documentation/24-account-export.md`; status (F-15 done); gaps;
Notion.

## 7. Testing

Real DB: signed-only list, date rule, parent filter with children, period, paging, scoping, archived, discarded,
not-ready note, invalid selection, removal, export, migration re-run and chains. Frontend: build + lint.
