# Structured Intake (F-31)

**Date:** 2026-11-12
**Status:** Approved for implementation
**Scope:** Backlog F-31: a guided first-session intake (anamnese) filled in by the provider, whose answers land in the
right record fields without retyping.

Done when (backlog): a completed intake fills the record with no retyping.

Out of scope: intake filled in by the patient (patient portal, second phase); AI suggestions from the transcript;
ready-made templates per approach (CBT, child) — gaps.

## 1. Decisions (agreed with the user, 2026-11-12)

| Topic | Decision |
|---|---|
| Who fills it | The provider, conducting the first session. |
| Questions | A default Anamnys template (6 sections, ~35 questions in Portuguese) the provider can adapt into their own versioned copy: switch questions off, add, reorder, edit text/type/destination. |
| Result | A signed note of a new format `Anamnese` (one section per template section), through the normal review/sign flow — so it is in the integrity chain (F-45), the dossier (F-44) and search. Answers with a record destination are listed after signing for one-by-one confirmation ("Levar para o prontuário"); nothing is written to the record without it. |
| Where / how often | "Iniciar anamnese" on the patient page (highlighted when there is none) and a shortcut in the Note Editor for the patient's first session; saved as a draft and resumable; a patient can have several intakes over time (re-assessment), each its own note. |

## 2. Data

Migration `design/migrations/2026-11-12-structured-intake.sql` (idempotent, bootstrap, migration-test chains after
`2026-11-10-appointment-series.sql`). Existing tables (schema only today): `IntakeTemplates` (`ProviderId`, `Name`,
`Version`, `Active`, `CreatedAt`), `IntakeQuestions` (`TemplateId`, `Key`, `Label`, `AnswerType`
text|number|date|boolean|choice, `Position`, `TargetField`, `Required`), `IntakeResponses` (`PatientId`, `SessionId`,
`QuestionId`, `Answer`, `RecordedAt`).

Changes:

- `IntakeTemplates`: `ProviderId` nullable (null only for the system default), `IsDefault` boolean, `BasedOnTemplateId`
  uuid (FK self), `ArchivedAt` timestamptz; a provider's preferred template is `ProviderIntakeSettings`
  (`ProviderId` PK, `DefaultTemplateId`). Unique (ProviderId, Name, Version) kept; a partial unique index allows one
  `IsDefault` template.
- `IntakeQuestions`: `SectionKey` text NOT NULL, `SectionTitle` text NOT NULL, `HelpText` text, `Choices` text[]
  (required when `AnswerType = 'choice'`), `Disabled` boolean default false. `TargetField` check: null or one of
  `patient.dateOfBirth`, `patient.phone`, `patient.contactEmail`, `medication`, `diagnosis`, `plan.objective`.
- New `Intakes` (`Id`, `ProviderId`, `PatientId`, `TemplateId`, `NoteId` uuid null FK `Notes`, `Status`
  `draft|signed`, `CreatedAt`, `UpdatedAt`, `SignedAt`).
- `IntakeResponses`: `IntakeId` uuid NOT NULL FK `Intakes` (on delete cascade), `UpdatedAt`; unique (`IntakeId`,
  `QuestionId`). `PatientId`/`SessionId` kept for compatibility.
- New `IntakeTransfers` (`Id`, `IntakeId`, `QuestionId`, `TargetField`, `Value`, `Status` `pending|confirmed|ignored`,
  `DecidedAt`, `ResultId` uuid null — the created record row, `ExcerptRefId` uuid null).
- `Notes_Format_ck` gains `Anamnese`; `Notes` gains `IntakeId` uuid null (FK).
- Audit actions `intake_started`, `intake_signed`, `intake_transfer_confirmed`, `intake_transfer_ignored`,
  `intake_template_adapted`. Discard deletes intakes, responses and transfers of the patient.
- Seed: the default template (`IsDefault = true`, `ProviderId` null, name "Anamnese padrão", version "1") with sections
  `identificacao`, `queixa`, `historia`, `saude`, `contexto`, `objetivos` and ~35 questions; questions such as date of
  birth, phone, e-mail, current medications, previous diagnoses and goals carry their `TargetField`.

## 3. Rules

- **Templates.** The default template is read-only. "Adaptar" copies it (or another own template) into a new provider
  template with `BasedOnTemplateId`. Editing an own template that already has intakes creates a new version (copy with
  `Version + 1`, the old one archived), so existing intakes keep pointing at the questions they had. Unused own
  templates are edited in place. Question keys are unique within a template version.
- **Starting.** `POST patients/{id}/intakes` creates an `Intakes` row (`draft`) with the provider's default template
  (or the system default) and writes `intake_started`. Several intakes per patient are allowed; at most one `draft` at a
  time per patient (409 `intake_in_progress`).
- **Answers.** `PUT intakes/{id}/answers` upserts a batch `[{ questionId, answer }]`; validation by type (number,
  ISO date, boolean, one of `Choices`); questions not in the intake's template or disabled are refused. Only `draft`
  intakes accept answers.
- **Note generation.** `POST intakes/{id}/note` (draft only; 400 `required_missing` with the missing question ids when
  a required question is empty) creates — or regenerates, while the note is unsigned — a note of format `Anamnese`
  linked by `Notes.IntakeId`, one section per template section in template order, keyed by `SectionKey`, content
  "Label: answer" lines for answered, enabled questions. The provider edits and signs it in the Note Editor like any
  note. Signing a note with `IntakeId` accepts the template's section keys (instead of the fixed format keys), marks
  the intake `signed`, writes `intake_signed`, and creates one `IntakeTransfers` row (`pending`) per answered question
  with a `TargetField`. The note is sealed (F-45) by the normal signing path.
- **Transfers.** `POST intakes/{id}/transfers/{transferId}/confirm` writes to the destination in one transaction and
  sets `confirmed`, `ResultId`, an `ExcerptRef` pointing at the note section that holds the answer (F-03 rule), and the
  audit entry (ids only):
  - `patient.dateOfBirth` / `patient.phone` / `patient.contactEmail`: update the patient (response shows the current
    value beforehand: `GET .../transfers` returns `currentValue`);
  - `medication`: a medication entry (F-09) with the answer as the drug text and the note as the source;
  - `diagnosis`: a patient diagnosis with the answer as description;
  - `plan.objective`: a treatment plan objective (F-14).
  `.../ignore` sets `ignored`. Confirm/ignore only on `pending` transfers of a `signed` intake (409 otherwise).
- **Discard / archive.** Discarding the patient deletes intakes, responses and transfers; archived patients can still
  open intakes but not start new ones.

## 4. Endpoints

All under `/api/phi/providers/me`, provider-scoped, 401 first, foreign ids 404, `no-store`.

| Route | Answer |
|---|---|
| `GET intake-templates` | `[ { id, name, version, isDefault, basedOnTemplateId, archived, inUse } ]` and the provider's `defaultTemplateId` |
| `GET intake-templates/{id}` | template with `sections: [ { key, title, questions: [ { id, key, label, helpText, answerType, choices, required, targetField, disabled, position } ] } ]` |
| `POST intake-templates/{id}/adapt` | 201 new own template |
| `PUT intake-templates/{id}` | own templates only (403 for the default); body = full question list; returns the (possibly new-version) template |
| `PUT intake-settings` | `{ defaultTemplateId }` |
| `GET patients/{patientId}/intakes` | `[ { id, status, createdAt, signedAt, noteId, pendingTransfers } ]` newest first |
| `POST patients/{patientId}/intakes` | 201 `{ id }`; 409 `intake_in_progress`; 400 `patient_archived` |
| `GET intakes/{id}` | intake + template sections + answers |
| `PUT intakes/{id}/answers` | 200; 400 `invalid_answer` with question ids; 409 `not_draft` |
| `POST intakes/{id}/note` | `{ noteId }`; 400 `required_missing` |
| `GET intakes/{id}/transfers` | `[ { id, questionLabel, targetField, value, currentValue, status } ]` |
| `POST intakes/{id}/transfers/{transferId}/confirm` / `ignore` | 200; 409 `not_pending` / `not_signed` |

## 5. Provider UI

- Patient page: "Anamnese" `DetailCard` — latest intake (date, "Em preenchimento" / "Assinada", open link), history,
  "Iniciar anamnese" (highlighted when there is none), "N itens para levar ao prontuário" when transfers are pending.
- Intake page `/patients/$patientId/intake/$intakeId`: sections as collapsible blocks with top anchors and "Voltar ao
  topo" (the Analyses pattern); a field per answer type; per-block "Salvo"/"Salvando…" (answers saved on blur);
  footer "Gerar nota para revisão" → opens the note in the Note Editor. Signed intakes are read-only.
- "Levar para o prontuário" panel (on the intake page once signed, and linked from the patient card): each pending
  item with Confirm/Ignore; registration fields show current vs new value.
- Note Editor: on the patient's first session with no intake, a "Fazer a anamnese deste paciente" shortcut.
- Settings → Prontuário tab → "Modelos de anamnese": the default template (view + "Adaptar"), own templates (edit
  questions: on/off, add, reorder, label, help, type, choices, required, destination; archive), and the default choice.
- Copy in `pt.json` under `intake.*`.

## 6. Testing

- Seed: default template present with 6 sections and questions; read-only (403).
- Adapt and versioning: adapting copies; editing an unused own template edits in place; editing a used one creates a
  new version and old intakes keep their questions.
- Intake lifecycle: start (one draft at a time, archived patient refused), answers saved and validated per type,
  required missing blocks note generation, note regenerated while unsigned, signing accepts template section keys,
  marks the intake signed, seals the note, creates transfers.
- Transfers: confirm writes each destination with audit and an excerpt ref; ignore writes nothing; nothing before
  signing; registration fields return current values.
- Discard removes intakes; scoping; 401; migration and chains. Front: provider build and lint.

## 7. Documentation

- Chapter `documentation/43-structured-intake.md`; index; status (F-31); gaps (patient-filled intake; AI suggestions;
  approach-specific templates); Confluence (F-31).
