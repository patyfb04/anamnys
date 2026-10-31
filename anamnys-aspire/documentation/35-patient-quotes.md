# Chapter 35: Patient Quotes (F-15)

How a provider records a passage of a note as a patient quote, how the quotes are listed over time on the
"Análises" page, and what the account export carries. Last checked against the code on 2026-10-31 (branch
`feature/patient-quotes`). Design: `design/specs/2026-10-30-patient-quotes-design.md`.

What a future change has to respect: a quote exists only because the provider marked it, never by rule or by the
model (section 4); the wording is always "que você registrou" (section 2); the list shows a quote only while its
exact text is still in the signed note (section 4); and no patient data goes in a URL beyond ids (section 7).

---

## 1. Purpose

Done when (backlog): quotes from different sessions appear side by side, with dates. The provider selects a passage
of a note and marks it "Marcar como fala do paciente", optionally with a theme. The card "Falas do paciente que
você registrou" on "Análises" lists the recorded quotes in chronological order, each with its session date and
origin (note and section).

---

## 2. Wording rule

The text is always "que você registrou", with the fixed sentence "Estas são as falas do paciente que você
registrou nas notas." Never "as palavras do paciente": that would suggest the system listened (README section
4d). No interpretation and no colours.

---

## 3. Data and migration

Migration `design/migrations/2026-10-30-patient-quotes.sql` (idempotent, mirrored in `anamnys-db-script.sql`).
**It must be applied to any persistent environment.** It:

- adds `PatientQuotes."AddedById"`, `"RemovedAt"` and `"RemovedById"`, with the check `PatientQuotes_Removed_ck`
  (the two removal columns are both null or both set). There are no foreign keys on the two user columns, as in
  `ThemeTags`;
- adds the audit actions `quote_recorded` and `quote_removed` to `AuditEntries_subject_ck`, with the same guard
  pattern as earlier migrations.

`PatientQuotes` and its index on `NoteId` already existed, and the F-06 discard already deletes the quotes.
`PatientQuotes` has no `ProviderId`: every read and write goes through the note. The migration is appended to the
chains of the other migration tests, after `2026-10-30-plan-adherence.sql`.

---

## 4. Marking rules and note states

- **Who marks.** The provider who owns the note, on a patient that is not discarded (404) and not archived (409
  `archived`).
- **Note state.** The note must be in review or signed (`ReadyForReview`, `Signed` or `Exported`); otherwise 409
  `note_not_ready`.
- **The text is the note's passage.** It is taken from the exact `ExcerptRef` created from the offsets, never from
  the request. The `quote` in the request only confirms that the offsets still point at what the client saw
  (ordinal comparison with whitespace runs collapsed); a difference is refused with `invalid_selection`. A quote
  is at most 200 characters, and so is the selection.
- **Theme.** Optional. It must be an active term of the active vocabulary, otherwise 400 `term_inactive`.
- **Check order.** 404, archived, note not ready, inactive term, invalid selection.
- **Removal.** `DELETE` marks the quote removed (`RemovedAt`, `RemovedById`); the row and its `ExcerptRef` stay. It
  is allowed on signed notes, audited as `quote_removed`, and a repeat is a 204 with no second audit.
- **Audit.** `quote_recorded` (field `quote`, new value the quote id, `NoteId` set) and `quote_removed`. Ids only,
  never the text.

### Signed-only list and the text-still-in-note rule

The patient list shows quotes of **signed** notes only (`Signed` or `Exported`), and only quotes that have an
exact reference and whose exact text (case-sensitive) is still in a section of the note. A quote marked on a note in
review can have its passage edited away before signing; the signed note must never be misquoted, so such a quote
leaves the patient list. It stays in the note's
own list, so the provider can see and remove it. The same rule applies to the export of signed notes. A quote with
no reference (rows from before F-15, or a reference lost) is hidden from both lists and answers 404 on `DELETE`.

The date of a quote is the F-10 session date of its note (actual start, else appointment, else signing day, in the
practice's time zone). Order: note date, then recording time, then id.

### Parent filter

`termId` includes the term and its descendants at any depth, as in the case analysis.

---

## 5. Endpoints

All under `/api/phi`, provider-scoped, camelCase JSON, dates `yyyy-MM-dd`, absent fields sent as `null`.

The item (`PatientQuoteItem`); `section` is the section key, not the title, and may be null; `on` is the F-10
session date:

```json
{ "id": "5b…", "text": "voltou a acordar", "on": "2026-01-05", "noteId": "c3…", "excerptId": "e1…",
  "section": "evolucao", "term": { "termId": "aa…", "label": "Sono" } }
```

`POST providers/me/notes/{noteId}/quotes`:

```json
{ "sectionKey": "evolucao", "offsetStart": 11, "offsetEnd": 27, "quote": "voltou a acordar", "termId": "aa…" }
```

`sectionKey`, both offsets and `quote` are required; `termId` may be omitted. Returns 201 with the item.

`DELETE providers/me/quotes/{id}` returns 204.

`GET providers/me/notes/{noteId}/quotes` returns 200 `{ "items": [item] }`, any note state, ordered by recording,
not filtered by the note's text. Readable when the patient is archived.

`GET providers/me/patients/{id}/quotes?from=&to=&termId=&page=` returns 200:

```json
{ "items": [ { "id": "5b…", "text": "voltou a acordar", "on": "2026-01-05", "noteId": "c3…", "excerptId": "e1…",
               "section": "evolucao", "term": null } ],
  "total": 14, "page": 1, "pageSize": 12 }
```

`from` and `to` are inclusive on `on`. A page below 1 is treated as 1; past the end gives empty `items` with the
real `total`. Readable when the patient is archived.

Errors. A 400 is a validation problem with `errors` and a top-level `code` where one is listed:

| Case | Status | `code` | `errors` key |
|---|---|---|---|
| Missing field, quote over 200, bad offsets, unknown section, `quote` differs from the text at the offsets | 400 | `invalid_selection` | `offsetStart` |
| Term not active, not in the active vocabulary, or unknown | 400 | `term_inactive` | `termId` |
| `from` after `to` | 400 | | `from` |
| Another provider's or discarded patient, unknown note, unknown quote (also a quote with no reference) | 404 | | |
| Archived patient (post and delete; delete checks it before the already-removed case) | 409 | `archived` | |
| Note not in review or signed | 409 | `note_not_ready` | |

---

## 6. Export

The account export (chapter 24) carries `pacientes/NNN-XX/falas-registradas.csv` with the columns
`id;fala;nota_id;data_nota;secao;tema;registrada_em`, and `paciente.json` gains `falasRegistradas` with `id`,
`fala`, `notaId`, `dataNota`, `secao`, `tema` and `registradaEm`. Removed quotes are left out. A quote of a signed
note is exported only while its exact text is in a section of that note; quotes of notes still in review are
exported as they are, and so are quotes with no reference, because the export must carry what the provider
recorded. `data_nota` uses the export's existing note date, as in the notes and themes files. The LEIA-ME
documents the new file and also `pontosParaRetomar`, which it was missing.

---

## 7. Provider app

- **Note view** (`NoteSections.tsx`, `NoteQuotes.tsx`). "Marcar como fala do paciente" in the selection actions,
  next to the theme and follow-up buttons, with the same 200-character limit. It is offered only for notes in
  review or signed, on a patient that is not archived. A form shows the passage with an optional `TermSelect`;
  "Registrar fala" is disabled while the request is in flight. The note's quotes are listed with "Remover". An
  archived patient sees the list and a read-only line. Server codes map to `patientQuotes.errors.*`.
- **Card** (`QuotesCard.tsx`) on "Análises", right below "Temas por sessão": the fixed sentence, the existing
  `ThemeFilter`, the page period, 1 to 3 columns, 12 per page, paging in the style of the trajectory event list.
  It also shows when the patient is below the analysis minimum, because the card has no such rule. Removing the
  last quote of the last page returns to the last page that has quotes.
- **Cell detail** (`OccurrenceModal.tsx`). That term's quotes in that session, read from the patient list through
  `patientQuotesApi.forDay` (all pages), so it follows the still-in-the-note rule. A collapsed parent includes its
  children.
- **Copy and cache.** Texts in `patientQuotes.*` of `pt.json`. Writes invalidate the `["quotes"]` prefix, which
  covers the note, the card and the cell queries. Hover styling uses classes.
- **URLs.** Only ids; the note link uses `?excerpt=<uuid>`.

---

## 8. Dev seed

`dev-seed/analysis-samples.sql` has a fourth block for the seeded patients: one quote on each of seeded notes 2
("conversa difícil com a mãe", Família), 4 ("jornada de trabalho longa", Sobrecarga de jornada), 6 ("discussão
com o colega de equipe", Conflito no trabalho) and 11 ("falecimento da tia", Luto). Each quote is a phrase that
literally appears in the note's `data` section, with an exact `ExcerptRef` (offsets by `strpos`), the matching
theme, `RecordedAt` = the note's signing time and the provider as author. A patient that already has a quote on
these notes is skipped. Apply the migration before the seed.

---

## 9. Tests

On the server, with real PostgreSQL:

- `PatientQuotesTests` (scratch database built from the bootstrap script): record and audit, note states, note not
  ready, archived, discarded, foreign, invalid selections, inactive term, signed-only list, date rule, order,
  parent filter with descendants, period, paging, scoping, the note list, removal and repeat, a passage edited
  away (gone from the patient list, kept in the note list), and the export. One HTTP test checks the real
  responses for 409 `archived`, 400 `invalid_selection` and 400 `term_inactive`. 409 `note_not_ready` is covered
  at service level only.
- `PatientQuotesMigrationTests`: chain against the bootstrap, re-run, earlier file re-run, rows kept, audit
  actions, removal check.
- `DiscardServiceTests` (quotes in the term counts), `AccountExportBuilderTests` (new file and LEIA-ME) and
  `ExcerptRequireArchitectureTests`, which now scans `Analysis/` for classes that query derived items without
  RequireExcerpt.
- The F-03 rule applied on 2026-10-31 also changed `CaseAnalysisQuery.CountedTags`: tags with no reference are not
  counted in the case analysis, the absences and the adherence (chapter 31).

The provider app was checked with build and lint only.

---

## 10. Limits and known gaps

- **Duplicates are not prevented on the server.** The same selection recorded twice creates two quotes; the UI
  only disables the button while the request is in flight.
- **The audit actions have no frontend labels.** `quote_recorded` and `quote_removed` (like the F-14 actions)
  appear in no audit view yet.
- **The theme filter lists the whole vocabulary,** including terms the patient has no quotes for.
- **No quotes when the cell detail opens from the absence card,** which passes no patient id.
- **Not exercised in a browser;** build and lint only.

---

## 11. Where things are

| Piece | Path |
|---|---|
| Migration | `design/migrations/2026-10-30-patient-quotes.sql` |
| Entity | `anamnys-aspire.Server/Data/Entities/PatientQuote.cs` |
| Query and contracts | `anamnys-aspire.Server/Analysis/PatientQuotes.cs` |
| Service | `anamnys-aspire.Server/Analysis/PatientQuotesService.cs` |
| Endpoints | `anamnys-aspire.Server/Analysis/PatientQuotesEndpoints.cs` |
| Export | `anamnys-aspire.Server/Exports/` (`AccountExportBuilder.cs`, `ExportData.cs`, `ExportReadme.cs`) |
| Card | `apps/provider/src/components/analysis/QuotesCard.tsx` |
| Note view | `apps/provider/src/components/notes/NoteQuotes.tsx` |
| API client | `packages/shared/src/api/analysis.ts` (`patientQuotesApi`) |
| Copy | `patientQuotes.*` in `packages/shared/src/lib/i18n/locales/pt.json` |
| Seed | `dev-seed/analysis-samples.sql` |
