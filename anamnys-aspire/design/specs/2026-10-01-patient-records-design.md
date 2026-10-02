# Patient Records (Create, Detail, Edit, Archive/Delete) Design

**Date:** 2026-10-01
**Status:** Approved
**Scope:** Provider-side patient record management on top of the patient list
(`2026-09-27-patient-list-design.md`): create a patient with clinical profile in a tabbed
modal, a patient detail page, editing basic data and clinical items, archiving, and
deleting records created by mistake. Adds the `PatientDiagnoses` entity and
`Patients.ArchivedAt`.

---

## 1. Context

- The provider app already has `/patients/new` (a full page) and `/patients/$patientId`
  (`PatientDetailView`), but both call `/api/patients…` routes that do not exist.
- `/patients/new` renders its own `TopBar` inside the `_app` shell, which already renders
  one: the top bar appears twice. It also asks for a preferred language, which is dropped.
- Clinical data in the schema:
  - Medications → `MedicationEntries` (`Drug`, `Dose`, `Posology`, `StartedOn` NOT NULL,
    `EndedOn`), FK to `Patients` `ON DELETE CASCADE`.
  - Treatment plan → `TreatmentPlans` (no text column) + `PlanObjectives.Description`.
  - Diagnoses → **no table**. The only `DiagnosisCode` is on `BillingCodes` (per note).
- Retention: the schema encodes Res. CFP 001/2009 (`RetentionRules.GuardYears >= 5`) and
  every clinical-record FK to `Patients` is `ON DELETE RESTRICT` (`Notes`,
  `Appointments`, `Sessions`, `ClinicalDocuments`, `ExternalDocuments`, `Dossiers`,
  `DisclosureAuthorizations`, `DisposalRecords`).

## 2. Decisions

| Topic | Decision |
|---|---|
| Create UX | Modal over the list, two tabs: *Dados básicos*, *Perfil clínico*. `/patients/new` removed. Full screen below `md`. |
| Clinical inputs | Row lists with "+ Adicionar": diagnoses (description + optional ICD), medications (drug, dose, posology, start), plan objectives (text). |
| Diagnoses storage | New table `PatientDiagnoses`. |
| Email / phone | Not editable here: `Patients.Email` is the portal login (globally unique, bound by the patient invite / self-registration flow). Shown read-only on the detail page. |
| Delete | Hard delete only when the patient has no clinical records and no portal account; otherwise `409` and the UI offers **Arquivar**. |
| Archive | `Patients.ArchivedAt`; archived patients are hidden from the list unless the "Somente arquivados" filter is on; reversible. |
| Detail page | Adapted from `specs/UI/Patient Detail`: header + actions, contact, visits, diagnoses, medications, plan objectives, recent notes. Theme chart, document upload, dossier and "start session" are out (no backing features). |
| Edit | Basic data via the same modal (basic tab only). Clinical items edited in place on the detail page (add, edit, resolve/end, remove). |
| Not found | A patient owned by another provider is `404`, never `403` — existence is not disclosed. |
| Out of scope | Logging PHI reads to `AccessLogs` (table exists, nothing writes it yet). |

## 3. Schema

Added to `anamnys-db-script.sql`:

```sql
-- in CREATE TABLE "Patients"
"ArchivedAt" timestamp with time zone,

CREATE TABLE "PatientDiagnoses" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"PatientId" uuid NOT NULL,
	"Description" text NOT NULL,
	"IcdCode" text,
	"RecordedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ResolvedOn" date
);
CREATE INDEX "PatientDiagnoses_PatientId_idx" ON "PatientDiagnoses" ("PatientId");
ALTER TABLE "PatientDiagnoses" ADD CONSTRAINT "PatientDiagnoses_PatientId_fkey"
	FOREIGN KEY ("PatientId") REFERENCES "Patients"("Id") ON DELETE CASCADE;
```

`DatabaseInitializer` only bootstraps an empty database, so existing databases (every
developer's dev volume, which the test fixture also uses) need
`design/migrations/2026-10-01-patient-records.sql`, an idempotent script that adds the
column, the table, its index and FK, and the `Notes_Patient_Created_idx` index from the
patient-list change.

## 4. API

All routes under `/api/phi/providers/me/patients`, provider cookie only, provider id from
the session. Every handler resolves the patient with
`Id == id && ProviderId == providerId`; a miss is `404`.

| Method & path | Body | Result |
|---|---|---|
| `POST /` | `CreatePatientRequest` | `201` `{ id }` — patient, diagnoses, medications and (if any objectives) a plan with its objectives in one `SaveChanges` |
| `GET /{id}` | — | `200` `PatientDetailResponse` |
| `PUT /{id}` | `UpdatePatientRequest` | `204` |
| `DELETE /{id}` | — | `204`, or `409` `{ message }` when it has clinical records or a portal account |
| `POST /{id}/archive` | — | `204` (idempotent) |
| `POST /{id}/unarchive` | — | `204` (idempotent) |
| `POST /{id}/diagnoses` | `DiagnosisInput` | `201` `{ id }` |
| `PUT /{id}/diagnoses/{itemId}` | `DiagnosisInput` | `204` |
| `DELETE /{id}/diagnoses/{itemId}` | — | `204` |
| `POST /{id}/medications` | `MedicationInput` | `201` `{ id }` |
| `PUT /{id}/medications/{itemId}` | `MedicationInput` | `204` |
| `DELETE /{id}/medications/{itemId}` | — | `204` |
| `POST /{id}/objectives` | `ObjectiveInput` | `201` `{ id }` — creates an open `TreatmentPlan` if none |
| `PUT /{id}/objectives/{itemId}` | `ObjectiveInput` | `204` |
| `DELETE /{id}/objectives/{itemId}` | — | `204` |

Contracts:

```
CreatePatientRequest  { firstName, lastName, dateOfBirth, diagnoses[], medications[], treatmentObjectives[] }
UpdatePatientRequest  { firstName, lastName, dateOfBirth }
DiagnosisInput        { description, icdCode?, resolvedOn? }
MedicationInput       { drug, dose?, posology?, startedOn, endedOn? }
ObjectiveInput        { description }

PatientDetailResponse {
  id, firstName, lastName, dateOfBirth, email?, phone?, hasPortalAccount,
  archivedAt?, lastVisit?, nextAppointmentAt?, noteStatus, canDelete,
  diagnoses:   [{ id, description, icdCode?, recordedAt, resolvedOn? }],
  medications: [{ id, drug, dose?, posology?, startedOn, endedOn? }],
  objectives:  [{ id, description, createdAt }],           -- latest open plan
  recentNotes: [{ id, status, format, createdAt, signedAt? }]  -- 10 newest
}
```

Validation (`400` `ValidationProblem`, Portuguese, camelCase keys; list items keyed
`diagnoses[0].description` etc.):

- `firstName`, `lastName`: required, ≤ 255.
- `dateOfBirth`: required, not in the future, not before 1900-01-01.
- `description` / `drug`: required, ≤ 500. `icdCode` ≤ 10. `dose`, `posology` ≤ 200.
- `startedOn`: required, not in the future. `endedOn` ≥ `startedOn`, not in the future.
  `resolvedOn` not in the future.
- At most 50 items per list on create.

Delete rule (`canDelete`): no portal account (`ExternalSubject IS NULL`) and no rows for
the patient in `Notes`, `Appointments`, `Sessions`, `ClinicalDocuments`,
`ExternalDocuments`, `Dossiers`, `DisclosureAuthorizations`, `DisposalRecords`,
`ScaleApplications`, `IntakeResponses`, `RecordingConsents`. Diagnoses, medications and
plans typed in by the provider cascade with the patient — they are exactly what a
mistaken registration contains.

Search: `PatientSearchRequest` gains `archived: bool` (default `false`): `false` returns
only active patients, `true` only archived ones.

## 5. Backend layout

- `Data/Entities/`: `PatientDiagnosis`, `MedicationEntry`, `TreatmentPlan`,
  `PlanObjective`; `Patient.ArchivedAt`.
- `Patients/PatientRecordContracts.cs`: requests (+ `Validate(DateOnly today)`), responses.
- `Patients/PatientRecords.cs`: static data operations taking
  `(AnamnysDbContext db, Guid providerId, …)`, returning a small result type
  (`Ok`, `NotFound`, `Conflict`) so endpoints stay thin and tests hit the rules directly.
- `Patients/PatientRecordEndpoints.cs`: the routes above.

## 6. Frontend

- `patientsApi`: `create`, `get`, `update`, `remove`, `archive`, `unarchive`, and
  `diagnoses|medications|objectives.{add,update,remove}`.
- `components/patients/Modal.tsx`: overlay + panel, Esc / backdrop close, focus moves
  into the panel, full screen below `md`.
- `components/patients/PatientFormModal.tsx`: create mode (two tabs, row lists) and edit
  mode (basic tab only). Field-level errors from the `400` body; switches to the tab
  holding the first error. Closing with unsaved input asks for confirmation.
- `routes/_app/patients/$patientId/index.tsx`: detail page; cards in
  `components/patients/detail/`. Archive / delete confirmations use the shared
  `alert-dialog`. After delete → back to the list.
- `routes/_app/patients/new.tsx` and `components/PatientDetailView.tsx` removed.
- List page: full width (no `max-w-6xl`); "Adicionar paciente" opens the modal;
  "Somente arquivados" checkbox in the filter panel.

## 7. Testing

- Unit: request validation.
- Postgres (shared AppHost fixture, one provider per test): create writes all four
  tables atomically; get returns the aggregate; another provider gets `NotFound` on every
  operation; update; delete succeeds without records and conflicts with a note, an
  appointment or a portal account; archive hides from search and `archived: true` shows
  it; clinical item add/update/remove and ownership through the patient.
- HTTP: `401` without session, `201`/`200`/`404`/`400` happy and error paths.
- Browser: create via modal, detail page, edit, add/resolve items, archive/unarchive,
  delete.

---

## 8. Addendum (2026-10-02): contact email, and no binding by email

**Problem.** `Patients.Email` is the patient-portal login: globally unique, and
`FirstLoginProvisioner` bound a new portal login to any unclaimed row with the same
email. Storing the provider-entered email there would (1) silently attach a future portal
sign-up to that provider's record, (2) fail a second provider's registration of the same
person with a uniqueness error that discloses they are someone else's patient, and
(3) hand portal access to whoever owns a mistyped email.

**Decision.**

- New column `Patients.ContactEmail` (text, not unique). Required by the API on create and
  update; used for scheduling notices only, never clinical content. Search, the email
  filter and the list subtitle use it.
- `Patients.Email` stays the portal login, set only together with `ExternalSubject`:
  `CONSTRAINT "Patients_Login_ck" CHECK (("Email" IS NULL) = ("ExternalSubject" IS NULL))`.
- `FirstLoginProvisioner.ResolvePatientAsync` no longer binds by email. A login with no
  row bound to its subject always creates its own row (still only with a verified email).
- Migration `design/migrations/2026-10-02-patient-contact-email.sql` moves `Email` to
  `ContactEmail` on rows without a login, then adds the constraint.

**Deferred to its own spec:** opt-in portal invitation from the provider record (single-use
token sent to `ContactEmail`, binding only through the token, statuses "não convidado /
convite enviado / acesso ativo"), splitting portal account from per-provider record so one
login can relate to several providers, and the portal showing past/upcoming sessions per
provider (never clinical content).
