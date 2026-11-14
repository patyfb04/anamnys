# Chapter 43: Structured Intake (F-31)

How the provider conducts a guided first-session intake (anamnese) from a questionnaire, turns it into a signed note of
the format `Anamnese`, and then carries individual answers into the patient record (registration data, medication,
diagnosis, treatment objective) one by one, without retyping. Last checked against the code on 2026-11-14 (branch
`feature/structured-intake`, not yet merged; sections 4 to 7 and 12 were compared with the C# in `Intake/` after the
final review). Design: `design/specs/2026-11-12-structured-intake-design.md`.

What a future change has to respect: the intake is signed **through its note**, in the normal review and sign flow, so it
is sealed in the integrity chain and appears in the dossier and in search like any note (section 5); answers are locked
as soon as the note exists, and unlocking deletes the unsigned note (section 4); nothing reaches the record without a
click on "Levar para o prontuário", and only for answers that are still in the signed text (section 6); a template that
has intakes is never edited in place, it is versioned (section 3); and audit values of intake actions carry ids, targets
and counts only, never answers (section 12).

---

## 1. Purpose and scope

F-31 (structured intake): a guided first-session anamnese filled in by the provider, whose answers land in the right
record fields without retyping. Done when (backlog): a completed intake fills the record with no retyping.

Out of scope (see section 14): intake filled in by the patient (patient portal, second phase); AI suggestions from the
transcript; ready-made templates per approach (CBT, child).

---

## 2. Decisions

| Topic | Decision |
|---|---|
| Who fills it | The provider, conducting the first session. |
| Questions | A default Anamnys template (6 sections, 36 questions in Portuguese) the provider can adapt into their own versioned copy: switch questions off, add, remove, reorder, edit text, type and destination. |
| Result | A signed note of the new format `Anamnese` (one section per template section), through the normal review and sign flow. Answers with a record destination are listed after signing for one-by-one confirmation ("Levar para o prontuário"); nothing is written to the record without it. |
| Where, how often | "Iniciar anamnese" on the patient page (highlighted when there is none) and a shortcut in the Note Editor for the patient's first session. Saved as a draft and resumable; a patient can have several intakes over time (re-assessment), each with its own note; at most one draft at a time. |
| Draft discard (ruling) | A draft intake can be discarded (`DELETE`): its answers and its unsigned note go. A stale draft must not block the patient. A signed intake is never deleted by this route. |
| Locking (ruling) | Once the note is generated, the answers are locked. "Editar respostas" unlocks them and **deletes the unsigned note** (the UI warns that its edits are lost); the note is then generated again from the answers. So the transfers always match the signed text: the record never receives a value the provider did not sign. |
| Transfers (ruling) | Created at signing only for answers still present in the signed section text. An answer the provider edited out of the note is not offered for the record. |
| Medication source (ruling) | `MedicationEntries` gains `SourceNoteId` (nullable FK to `Notes`), set by an intake medication transfer, so the medication timeline can show "da anamnese" and link to the note. |
| Namespace | `Anamnys.Server.Intakes` (folder `Intake/`): `Anamnys.Server.Intake` would shadow the `Intake` entity. |

---

## 3. Data and migration

Migration `design/migrations/2026-11-12-structured-intake.sql` (idempotent, one transaction, seed included, mirrored in
`anamnys-db-script.sql` and appended to the chains of the other migration tests after `2026-11-10-appointment-series.sql`).
**It must be applied to any persistent environment before the new server version runs.** The `Note` entity now maps
`IntakeId`, so until the migration is applied **every note query fails** (`42703: column n.IntakeId does not exist`), not
only the intake routes; the dev-database suites that touch notes fail for the same reason. The migration:

- `IntakeTemplates`: `ProviderId` nullable (null only for the system default), `IsDefault`, `BasedOnTemplateId` (self FK,
  SET NULL), `ArchivedAt`; check `IntakeTemplates_Owner_ck` (`IsDefault` = `ProviderId IS NULL`); partial unique index
  `IntakeTemplates_OneDefault_key` (one default); (`ProviderId`, `Name`, `Version`) stays unique.
- `IntakeQuestions`: `SectionKey` and `SectionTitle` NOT NULL, `HelpText`, `Choices text[]`, `Disabled`; check
  `IntakeQuestions_TargetField_ck` (null or one of the TargetFields below) and `IntakeQuestions_Choices_ck` (a `choice`
  question needs at least one choice); index (`TemplateId`, `Position`).
- `ProviderIntakeSettings` (`ProviderId` PK to `Providers`, `DefaultTemplateId` to `IntakeTemplates`, both CASCADE).
- `Intakes` (`Id`, `ProviderId`, `PatientId`, `TemplateId`, `NoteId` null FK `Notes` SET NULL, `Status` `draft|signed`,
  `CreatedAt`, `UpdatedAt`, `SignedAt`); `Intakes_Signed_ck`; FK (`PatientId`, `ProviderId`) to `Patients` CASCADE; partial
  unique `Intakes_OneDraft_key` (one draft per patient); trigger `Intakes_patient_not_discarded`.
- `IntakeResponses`: `IntakeId` NOT NULL (FK `Intakes`, CASCADE), `UpdatedAt`, unique (`IntakeId`, `QuestionId`).
  `PatientId` and `SessionId` are kept for compatibility. Pre-existing rows without an intake are deleted (no code ever
  wrote them).
- `IntakeTransfers` (`Id`, `IntakeId`, `QuestionId`, `TargetField`, `Value`, `Status` `pending|confirmed|ignored`,
  `DecidedAt`, `ResultId`, `ExcerptRefId` FK `ExcerptRefs` SET NULL); `IntakeTransfers_Decided_ck`; unique (`IntakeId`,
  `QuestionId`).
- `Notes_Format_ck` gains `Anamnese`; `Notes.IntakeId` (FK `Intakes`, SET NULL; partial unique `Notes_IntakeId_key`).
- `MedicationEntries.SourceNoteId` (FK `Notes`, SET NULL, partial index).
- Audit actions added to `AuditEntries_subject_ck`: `intake_started`, `intake_signed`, `intake_transfer_confirmed`,
  `intake_transfer_ignored`, `intake_template_adapted`, and, from the ruling round, `intake_discarded` and
  `intake_answers_unlocked`.
- Seed: the default template, id `0f310000-0000-4000-8000-000000000001`, "Anamnese padrão", version 1, `IsDefault`,
  `ProviderId` null.

**TargetField list** (null, or exactly one of): `patient.dateOfBirth`, `patient.phone`, `patient.contactEmail`,
`medication`, `diagnosis`, `plan.objective`.

**The default template: 6 sections, 36 questions.**

| Section key | Title | Questions | Notes |
|---|---|---|---|
| `identificacao` | Identificação | 7 | `data_nascimento` (date) to `patient.dateOfBirth`; `telefone` to `patient.phone`; `email` to `patient.contactEmail`; marital status, schooling, how the patient arrived (choices), occupation |
| `queixa` | Queixa principal | 6 | `queixa_principal` (required), onset, triggers, intensity 0 to 10 (number), daily impact, what was tried |
| `historia` | História pessoal e familiar | 7 | development, family composition, family relations, family mental-health history, marking events, previous treatment (boolean) and its details |
| `saude` | Saúde | 8 | `diagnosticos_previos` to `diagnosis`; `medicacoes` to `medication`; clinical conditions, sleep (choice), food, substances, physical activity, `risco` (suicidal ideation or self-harm, choice, required) |
| `contexto` | Contexto atual | 5 | work or studies, support network, relationships, leisure, routine |
| `objetivos` | Objetivos | 3 | `objetivos_terapia` to `plan.objective`; expectations; availability |

Answer types: `text`, `number`, `date`, `boolean`, `choice`.

---

## 4. Rules: templates, lifecycle, lock and unlock

**Templates.** The default template is read-only (403 `template_read_only` on edit or archive). "Adaptar" copies it, or
another own template, into a new own template with `BasedOnTemplateId`; without a name it is "Minha anamnese" (from the
default) or "{name} (cópia)", then " 2", " 3" and so on. Editing an own template that has intakes (`inUse`) creates a new
version (a copy with `Version + 1`, the old one archived, the provider default moved to it), so existing intakes keep
the questions they had. Unused own templates are edited in place; the editor may also remove questions, and the server
versions the used ones. Question keys are unique within a version; the questions of one section share one title.
Archived templates cannot be edited or chosen as default. The provider's preferred template is
`ProviderIntakeSettings`; null means the system default.

**Starting.** `POST patients/{id}/intakes` creates a `draft` intake with the provider's preferred template (or the
default) and writes `intake_started`. Refused for an archived patient (400 `patient_archived`) and when a draft already
exists (409 `intake_in_progress`).

**Answers.** `PUT intakes/{id}/answers` takes a bare array `[{ questionId, answer }]` (at most 200). The batch is
all-or-nothing: an unknown, disabled or repeated question, or a value that fails its type, makes the whole batch 400
`invalid_answer` with the question ids. An empty or null answer clears the answer. Stored forms: number invariant ("7,5"
is accepted and stored "7.5"); date `yyyy-MM-dd`; boolean `"true"` or `"false"`; choice equal to one of its choices;
text trimmed, at most 4000. A question with a destination is stricter: date of birth between 1900-01-01 and today, phone
at most 40, e-mail with a valid shape, medication, diagnosis and objective at most 500 characters. Only a `draft`
intake without a note accepts answers (409 `not_draft`, `answers_locked`, `patient_archived`).

**Note generation.** `POST intakes/{id}/note` (draft only) creates a note and locks the answers. 400 `required_missing`
with the missing question ids when a required question is empty. When the intake already has a note the call returns
that note's id and never rewrites it.

**Unlock** (`POST intakes/{id}/unlock`, "Editar respostas"). Deletes the unsigned note with its sections, the points
"para retomar" (F-14) marked on it, excerpt refs, notifications and its own note audit rows, and makes the answers
editable again; a point resolved in that note goes back to open (its `Resolved*` fields are cleared). A call without a
note is a 200 no-op.
Writes `intake_answers_unlocked` (ids only). The note audit rows go because they hold note text and
`AuditEntries_NoteId_fkey` is RESTRICT, the same as the record discard; the cost is that the edit history of the
discarded draft is lost. The next `POST note` creates a new note with a new id. Signed notes are never deleted.

**Discard** (`DELETE intakes/{id}`). Draft only (409 `not_draft` when signed): deletes the intake, its answers and its
unsigned note, as unlock does; audit `intake_discarded`.

**Locks.** Generate, unlock and discard take the patient key-share lock, then the note, then the intake (one helper,
`WithLocksAsync`), in that order.

---

## 5. Rules: note generation and signing

The generated note has format `Anamnese`, status `ReadyForReview`, input mode `Text`, and `IntakeId` set. Its sections
are the template's sections that have an enabled question, in template order, with the key equal to the `SectionKey`;
each content is "Label: answer" lines joined by newlines for the answered, enabled questions (dates as `dd/MM/yyyy`,
booleans as `Sim` or `Não`, numbers with the decimal comma, "7,5"; empty when unanswered). The note's own rows are the
usual note audit: `note_created` with value `Text,Anamnese` and `sections_created` with the section keys. The provider edits and signs it in the Note Editor like any note.
`Anamnese` is not a structurable format.

**Signing** uses the existing sign endpoint. For a note with `IntakeId`, `IntakeNote.SignAsync` runs inside the signing
transaction, before the status change, and:

- accepts the template's section keys instead of the fixed keys of the other formats. It answers Locked (the existing
  409 mapping) when the keys differ from the template's, when the intake is not a draft, or when `Format = Anamnese`
  and `IntakeId` disagree;
- marks the intake `signed` (`SignedAt`) and writes `intake_signed`;
- creates one `IntakeTransfers` row (`pending`) per answered destination question **whose whole generated line
  ("Label: answer", as displayed) is still a line of its section's signed text**; a line the provider changed ("...: nega
  TAG") or removed gives no transfer.

The note is sealed (F-45) by the normal signing path, untouched by this feature. Signing therefore puts the intake in
the integrity chain, the dossier (F-44) and record search like any signed note.

**Section titles.** The note detail (`GET notes/{id}`) returns `intakeId` and, per section, `title`: the template's
section title for `Anamnese` notes, null for the fixed formats. Every other output of the note uses the template titles
too, so an added section (key `secao_N`) never shows its key: the single-note PDF (`NoteExport`), the dossier PDF
(`DossierData.SectionTitles`), the account export PDF and JSON (`AccountData.SectionTitles`, `secoes[].titulo`) and
excerpt passages (`ExcerptService.GetPassageAsync`). The batched lookup is `IntakeNote.SectionTitlesByNoteAsync`.

---

## 6. Rules: transfers and excerpts

`GET intakes/{id}/transfers` lists the transfers by question order (empty before signing), with `currentValue` for the
registration fields (date of birth as `yyyy-MM-dd`; null for the other destinations).

`POST .../transfers/{transferId}/confirm` writes to the destination in one transaction, sets `confirmed`, `ResultId` and
`DecidedAt`, creates an `ExcerptRef` (F-03 rule) and writes the audit entry:

| TargetField | Writes | `ResultId` |
|---|---|---|
| `patient.dateOfBirth`, `patient.phone`, `patient.contactEmail` | updates the patient | the patient id |
| `medication` | a `MedicationEntry` (F-09): `Drug` = the answer, `StartedOn` = the signing date in São Paulo, `SourceNoteId` = the signed note | the entry id |
| `diagnosis` | a patient diagnosis with the answer as description | the diagnosis id |
| `plan.objective` | an objective on the open plan (`PatientRecords.OpenPlanAsync`); a plan is created if none is open | the objective id |

The excerpt ref points at the note, the section key of the question, with the quote the answer as written in the signed
note (`exact` when found). Its id is stored in `IntakeTransfers.ExcerptRefId`. `.../ignore` sets `ignored` and writes
nothing to the record. Confirm and ignore work only on `pending` transfers of a `signed` intake (409 `not_signed`,
`not_pending`); confirm is also refused for an archived patient (409 `patient_archived`).

**Medication timeline.** In `GET patients/{id}/timeline` a segment from an intake medication has
`origin = { kind: "intake", noteId, excerptId, documentId/documentKind/documentDate/page: null }`; the UI says "da
anamnese" and links to the note at the excerpt.

---

## 7. Rules: discard and archive

Discarding the patient (retention chapter 30) deletes the transfers, responses and intakes (before the notes), and
counts them in the disposal term; the trigger `Intakes_patient_not_discarded` blocks new ones on a discarded patient. A
patient with intakes counts as having clinical records for the hard delete (`PatientRecords`). An archived patient can
still open intakes and see their transfers. The server refuses starting (400 `patient_archived`), answering, generating
the note and confirming a transfer (409 `patient_archived`). Unlock, discard and ignore are **not** refused by the
server for an archived patient (they only remove or decline; nothing new enters the record); the intake page hides the
"Editar respostas" and "Descartar" buttons for an archived patient, so they are reachable only through the API.

---

## 8. Endpoints and codes

All under `/api/phi/providers/me`, cookie session, 401 first, foreign or missing ids 404, `Cache-Control: no-store`, JSON
camelCase. Codes come as `{ code, message }`, plus `questionIds` for `invalid_answer` and `required_missing`. Template
`PUT` and name problems are the standard validation problem (400, `errors` keyed `name`, `questions`,
`questions[i].key|sectionKey|sectionTitle|label|helpText|answerType|choices|targetField`).

| Method and path | Success | Errors |
|---|---|---|
| `GET intake-templates` | 200 `{ defaultTemplateId, templates: [{ id, name, version, isDefault, basedOnTemplateId, archived, inUse }] }` (default first, then active, by name and version) | |
| `GET intake-templates/{id}` | 200 summary + `sections: [{ key, title, questions: [{ id, key, label, helpText, answerType, choices, required, targetField, disabled, position }] }]` (disabled questions included) | 404 |
| `POST intake-templates/{id}/adapt` | 201 detail; body optional `{ name? }` | 400 validation; 404; 409 `name_taken` |
| `PUT intake-templates/{id}` | 200 detail (new id and version when the template was in use); body `{ name?, questions: [{ key, sectionKey, sectionTitle, label, helpText, answerType, choices, required, targetField, disabled }] }` | 400 validation; 403 `template_read_only`; 404; 409 `template_archived`, `name_taken` |
| `POST intake-templates/{id}/archive` | 200 summary | 403 `template_read_only`; 404 |
| `PUT intake-settings` | 200 `{ defaultTemplateId }`; body `{ defaultTemplateId: uuid or null }` | 404; 409 `template_archived` |
| `GET patients/{patientId}/intakes` | 200 `[{ id, status, createdAt, signedAt, noteId, pendingTransfers }]`, newest first | 404 |
| `POST patients/{patientId}/intakes` | 201 `{ id }` | 400 `patient_archived`; 404; 409 `intake_in_progress` |
| `GET intakes/{id}` | 200 `{ id, patientId, status, createdAt, updatedAt, signedAt, noteId, noteStatus, pendingTransfers, template: { id, name, version }, sections (enabled questions only), answers: [{ questionId, answer, updatedAt }] }` | 404 |
| `PUT intakes/{id}/answers` | 200 `[{ questionId, answer, updatedAt }]` (all current answers) | 400 `invalid_answer`; 404; 409 `not_draft`, `answers_locked`, `patient_archived` |
| `POST intakes/{id}/note` | 200 `{ noteId }` | 400 `required_missing`; 404; 409 `not_draft`, `patient_archived` |
| `POST intakes/{id}/unlock` | 200 intake (as `GET`, `noteId` null) | 404; 409 `not_draft` |
| `DELETE intakes/{id}` | 204 | 404; 409 `not_draft` |
| `GET intakes/{id}/transfers` | 200 `[{ id, questionId, questionLabel, sectionKey, targetField, value, currentValue, status, decidedAt, resultId }]` | 404 |
| `POST intakes/{id}/transfers/{transferId}/confirm` | 200 transfer | 404; 409 `not_signed`, `not_pending`, `patient_archived` |
| `POST intakes/{id}/transfers/{transferId}/ignore` | 200 transfer | 404; 409 `not_signed`, `not_pending` |

Routes beyond the design's section 4: `archive`, `unlock`, `DELETE intakes/{id}`, and the codes `patient_archived`,
`name_taken`, `answers_locked`, `template_archived`, `template_read_only`. Also: the note detail fields `intakeId` and
section `title`, and `origin.noteId` on the timeline. All routes are documented in `anamnys-aspire.Server.http`.

---

## 9. Provider UI

- **Patient page:** `IntakeCard` ("Anamnese"), first in the clinical grid: the latest intake (badge "Em preenchimento" or
  "Assinada", date, "Continuar" or "Abrir"), "Anteriores", "Iniciar anamnese" (a filled button in a highlighted box when
  there is none, a small outline button in the header otherwise; hidden while a draft exists or for an archived
  patient), "N itens para levar ao prontuário" linking to `#levar-ao-prontuario`, and "Descartar rascunho" on draft rows.
- **Intake page** `/patients/$patientId/intake/$intakeId`: header with status, patient, date, template name and version;
  an anchor button per section; one collapsible `DetailCard` per section (open by default, state remembered per section
  key in `localStorage`) with "Voltar ao topo". A field per type: textarea (input, tel or email for registration targets),
  decimal text for numbers, date input (1900 to today for date of birth), Sim/Não toggle (click again clears), select
  for choices. Text, number and date save on blur; boolean and choice on change; saves are queued one at a time per
  intake; each block shows "Salvando…", "Salvo" or "Não salvo"; the stored form replaces the typed one. Required fields
  carry `*` and a legend. "Gerar nota para revisão" waits for the queue, refuses when an on-screen answer is unsaved,
  then opens the note; `required_missing` marks the fields, opens their blocks and scrolls to the first.
- **Draft with a note:** the answers are read-only; a banner offers "Abrir nota", "Editar respostas" (a confirmation says
  the note and its edits are deleted) and "Descartar rascunho". The form stays read-only until the patient is loaded, so
  an archived patient's intake never flashes editable.
- **Signed intake:** read-only, banner "Anamnese assinada em …" with "Ver nota assinada", and the transfers panel above
  the sections.
- **"Levar para o prontuário"** (`IntakeTransfersPanel`): each transfer with destination, question and value; registration
  fields show "No cadastro hoje" against "Da anamnese"; Confirmar and Ignorar per pending item; decided items show a
  badge. Confirm invalidates patient, patients, timeline, plan-adherence, intake and intakes queries.
- **Note Editor:** `IntakeShortcut` "Fazer a anamnese deste paciente" on `/note-editor/new` once a patient is chosen, and
  on an unsigned non-Anamnese note, when the patient has no intake and no signed note ("first session", section 14). The
  note page of an Anamnese note links "Ver as respostas da anamnese"; section headings use the server's `title`. Signing
  an Anamnese note invalidates the intake queries.
- **Timeline card:** origin "da anamnese" with the link "Ver na nota da anamnese".
- **Settings, Prontuário tab, "Modelos de anamnese"** (`IntakeTemplatesSection`): the Anamnys template (Ver perguntas,
  Adaptar, Usar nas novas anamneses) and "Meus modelos" (Ver perguntas, Editar, Adaptar, Arquivar, Usar nas novas
  anamneses); a badge marks the preferred one; archived ones are hidden. The editor (`IntakeTemplateEditor`, a modal):
  name; per section the title and up/down; per question on/off, up/down, remove, label, help, type, destination (choosing
  one sets the matching type; changing the type drops an incompatible destination), choices (one per line, at least one
  for `choice`), required; add section and question. New keys are generated (`pergunta_N`, `secao_N`), existing keys are
  kept. Server errors map back by flat index; an in-use template shows "salvo como versão N".
- Shared: `packages/shared/src/api/intake.ts`, types in `types.ts`, copy in `pt.json` under `intake.*` (plus
  `timeline.originIntake`, `noteView.sections` for the six default keys, `liveSession.formats.Anamnese`); hooks and
  helpers in `components/intake/intakeModel.ts`. `DetailCard` gained `defaultOpen` and `BackToTop` gained `targetId`
  (defaults unchanged).

---

## 10. Tests

Under `anamnys-aspire.Tests/Intake/` (`IntakeScene`, `IntakeTemplateTests`, `IntakeLifecycleTests`,
`IntakeDraftTests`, `IntakeTransferTests`, `IntakeEndpointTests`) and
`Data/StructuredIntakeMigrationTests.cs`, on scratch databases from the bootstrap:

- Seed: the default template with 6 sections and questions, read-only (403).
- Templates: adapt copies; an unused own template is edited in place; a used one gets a new version and old intakes keep
  their questions; `name_taken` in place and on rename.
- Lifecycle: start (one draft, archived patient refused); answers saved and validated by type; required missing blocks
  the note; the note is created once and locks the answers; signing accepts the template's section keys, marks the
  intake signed, seals the note and creates the transfers; Anamnese and `IntakeId` mismatch is Locked.
- Draft: unlock deletes the note and a new note follows the new answers; discarding a draft; discarding or unlocking a
  signed intake refused; an answer edited out of the note, or whose line was changed around it, gives no transfer
  (`ContainsLine`); numbers read with the decimal comma; unlock deletes the points "para retomar" marked on the draft
  and reopens those resolved in it, leaving other notes alone.
- Section titles (`IntakeSectionTitleTests`): an own template with an added section shows its title in the dossier
  note model, the account export PDF model and JSON, and an excerpt passage.
- Transfers: each destination is written with audit and an excerpt ref; ignore writes nothing; nothing before signing;
  registration fields return current values; the medication timeline origin and `SourceNoteId`.
- HTTP: mapping of `answers_locked`, `not_draft`, `not_pending`, `patient_archived`; scoping; 401 on every route.
- Discard of a patient removes the intakes (`DiscardServiceTests` area); the migration and every `*MigrationTests` chain
  (including the 19 `*_patient_not_discarded` triggers).

Runs: after fix round 1, `-class "*Intake*"` 41/41 and `-class "*MigrationTests*"` 169/169 passed (scratch databases).
Provider and patient build and lint clean. Not exercised against a running server or in a browser. The existing
dev-database suites that touch notes (`NoteEditingTests`, `NoteSigningTests`, `NoteExportTests`, `PatientRecordsTests`,
`PatientRecordEndpointTests`, `AccountExportBuilderTests`) failed 41 times in the first round because the migration was
not applied to the dev database; re-run them after applying it, with Aspire stopped. Final-review fix wave
(2026-11-14): `-class "*Intake*" -class "*Dossier*" -class "*AccountExport*" -class "*Excerpt*"` 234 total, 220 passed;
the 14 failures (13 `AccountExportBuilderTests`, `ExcerptEndpointTests.Passage_UnknownId_Returns404`) are the dev
database lacking `Notes.IntakeId` and `MedicationEntries.SourceNoteId`. Provider build and lint clean. Full suite:
pending.

---

## 11. Deployment

1. Apply `design/migrations/2026-11-12-structured-intake.sql` to every persistent environment **before** the new server
   version runs. Until then note queries fail (the `Notes.IntakeId` column), so notes, signing, export and the dossier
   break, not only the intake routes. The migration is idempotent and seeds the default template.
2. Stop Aspire before running the tests that use the dev database.
3. No new AppHost parameters or configuration.

---

## 12. Audit and privacy

| Action | Value |
|---|---|
| `intake_started` | `{intake} patient {patient} template {template}` |
| `intake_signed` | `{intake} note {note} transfers {count}` |
| `intake_transfer_confirmed` | `{transfer} intake {intake} {targetField} {resultId}` |
| `intake_transfer_ignored` | `{transfer} intake {intake} {targetField}` |
| `intake_template_adapted` | every change to an own template: `{copy} adapted from {source}` (adapt), `{template} edited` (in place), `{new} version {n} of {old}` (new version), `{template} archived` (archive) |
| `intake_answers_unlocked` | `{intake} note {note} deleted` |
| `intake_discarded` | `{intake} patient {patient} note {note or -} answers {count}` |

The generated note itself gets the normal note rows (`note_created` "Text,Anamnese", `sections_created` with the
section keys, then the edit and sign rows of any note); unlock and discard delete them with the note.

Intake audit rows have the provider as actor, `NoteId` null and `FieldChanged` "intake". Values carry ids, targets and
counts only; the answers are clinical text and stay in the responses and the signed note. The note audit rows of a note
discarded by unlock or discard are deleted with it (section 4).

---

## 13. Where things are

| Piece | Path |
|---|---|
| Migration | `design/migrations/2026-11-12-structured-intake.sql` |
| Entities | `anamnys-aspire.Server/Data/Entities/` (`IntakeTemplate`, `IntakeQuestion`, `Intake`, `IntakeResponse`, `IntakeTransfer`, `ProviderIntakeSettings`; `Note.IntakeId`, `MedicationEntry.SourceNoteId`) |
| Server code | `anamnys-aspire.Server/Intake/` (`IntakeContracts`, `IntakeTemplates`, `IntakeService`, `IntakeNote`, `IntakeTransfers`, `IntakeEndpoints`) |
| Hooks | `Notes/NoteEditing.cs` (signing), `Notes/NoteQueries.cs`, `Notes/NoteExport.cs`, `Notes/NotePdfModel.cs`, `NoteStructuring/NoteFormats.cs`, `Medications/MedicationTimeline.cs`, `Retention/DiscardService.cs`, `Patients/PatientRecords.cs` |
| Provider UI | `apps/provider/src/components/intake/`, `components/settings/IntakeTemplatesSection.tsx`, `routes/_app/patients/$patientId/intake.$intakeId.tsx` |
| API client | `packages/shared/src/api/intake.ts` |
| Tests | `anamnys-aspire.Tests/Intake/`, `anamnys-aspire.Tests/Data/StructuredIntakeMigrationTests.cs` |

---

## 14. Limits and known gaps

- The intake is filled only by the provider; the patient-portal version is a second phase.
- No AI suggestions from the transcript; no ready-made templates per approach.
- The account export does not include draft intakes (their answers and an unsigned note's intake link): drafts are
  not part of the record; a signed intake is exported through its note.
- Unlock and discard do not touch audit rows that are not keyed to the deleted note; the unlock warning mentions only
  note edits.
- "First session" is approximated as "the patient has no signed note" (one extra notes search per view); the server has
  no such notion.
- There is no unsaved-text warning when the tab is closed; answers are saved on blur.
- Unlock and discard lose the edit history of the deleted unsigned note.
- Transfers build the medication, diagnosis and objective inline instead of through shared constructors.
- The shortcut for an archived patient on `/note-editor/new` depends on the picker.
- No provider component tests exist; not checked in a browser or against a live server; the Confluence documentation is
  pending.
