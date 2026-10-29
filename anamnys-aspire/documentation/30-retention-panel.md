# Chapter 30: Retention and Compliance Panel (F-06)

How the app keeps one retention deadline per patient record, lists on one page what needs the provider's
attention (unsigned notes, records near or past the end of retention, documents with compliance pending),
alerts through the bell, and discards an expired record irreversibly with a disposal term. Last checked
against the code on 2026-10-28 (branch `feature/attendance-stats`, uncommitted).
Design: `design/specs/2026-10-28-retention-panel-design.md`.

What a future change has to respect: the clock only moves forward on a push and is rebuilt from the records
on a recompute (section 3), the discard is one transaction with object deletes after the commit
(section 8), the database refuses new rows for a discarded patient (section 9), no patient data goes into
logs, audit values, notification rows, dedupe keys or URLs (section 14), and the lists carry dates only,
with no severity colours and no ordering by risk (section 6).

---

## 1. Purpose

Done when (backlog): the provider sees, on one screen, every unsigned note and every record approaching
the retention limit. The page "Conformidade" has three blocks, a daily job puts new items on the bell, and
an expired record can be discarded from the page, leaving a disposal term as the proof.

---

## 2. Data

Migration `design/migrations/2026-10-28-retention-panel.sql` (idempotent, one transaction), mirrored in
`anamnys-db-script.sql`. **It must be applied to every persistent environment, and re-applied to one that
had its first version** (the trigger block and `DisposalTerms.RetentionUntil` were added in the review waves). Until it is applied, every
EF query on `Patients`, `Providers` or `Notifications` fails with a missing column, and so do the sign and
attend hooks.

| Object | Change |
|---|---|
| `Patients` | `LastRecordAt` date, `RetentionUntil` date, `DiscardedAt` timestamptz; index `Patients_Provider_RetentionUntil_idx` on (`ProviderId`, `RetentionUntil`). No SQL backfill: the job fills the clocks when the server starts |
| `Providers` | `UnsignedNoteDays` integer NOT NULL default 7, `Providers_UnsignedNoteDays_ck` (>= 1) |
| `Notifications` | `DedupeKey` text; unique partial index `Notifications_Provider_DedupeKey_key` on (`ProviderId`, `DedupeKey`) where `DedupeKey` is not null |
| `RetentionRules` | unchanged; the clock reads the row with `RecordKind = 'prontuario'` (absent: 5 years, `Res. CFP 001/2009`); `RetentionRules_GuardYears_ck` keeps it at 5 or more |
| `DisposalTerms` | new: `Id`, `ProviderId`, `PatientId`, `CreatedAt`, `ObjectKey`, `PeriodFrom`, `PeriodTo`, `GuardYears`, `Basis`, `Counts` jsonb, `RetentionUntil` date (the deadline that had ended, as the clock had it at the discard; a table from the first version of the file gets the column back-filled with `PeriodTo + GuardYears`) |
| `AuditEntries_subject_ck` | allows `patient_discarded` without a note or document id; guarded with `LIKE '%patient_discarded%'` so re-running an earlier migration does not narrow it |
| Triggers | function `"Patients_not_discarded"()` and a `BEFORE INSERT` trigger on 18 tables (section 9) |

`DisposalTerms` constraints: one term per patient (`DisposalTerms_PatientId_key`), `PeriodFrom <= PeriodTo`,
`GuardYears >= 5`, `Counts` an object, and a foreign key (`PatientId`, `ProviderId`) to `Patients` with
`ON DELETE RESTRICT`, so a patient row with a term cannot be deleted (SQLSTATE 23001). The patient hard delete
(`PatientRecords.DeleteAsync`) answers 404 for a discarded record, and maps both 23001 and 23503 to its 409
conflict.

Migration tests: `Data/RetentionPanelMigrationTests` (also checks that the previous schema plus the
migration run twice gives the same 18 triggers as the bootstrap), and the new file is added to the chain of
the other migration test classes.

---

## 3. The clock (`Retention/RetentionClock`)

One deadline per patient record: `RetentionUntil = LastRecordAt + guard years`. The date is the
practice-local day (America/Sao_Paulo) of the latest of four kinds of record:

- a note with `SignedAt` (signed or exported);
- a clinical document with `SignedAt`, superseded ones included, since they were issued;
- a session with `Attendance = 'attended'`, dated `ActualStart ?? CreatedAt`, as in the attendance
  statistics (chapter 29);
- an appointment with `Status = 'attended'`, dated by its start.

These four kinds are one SQL fragment, `RetentionClock.RecordDatesSql`, shared by the recompute (latest date)
and by the disposal term (`RetentionClock.PeriodAsync`, first and last date), so the two cannot drift apart.

Guard years are the provider's `prontuario` rule, default 5, never below 5, at most 100. The addition is a
Postgres interval, so 29 Feb 2024 plus 5 years gives 28 Feb 2029. A patient with no records has both
fields null and never appears in the retention block.

**Push.** `PushAsync(db, providerId, patientId, at, ct)` is one UPDATE that only moves the clock forward.
It is called, inside the caller's transaction, from:

- `NoteEditing.SignAsync` (note signing);
- `ClinicalDocumentService.SignAsync` (document signing, a correction included);
- `NoteSessions.AttendAsync` (a session attended: live finalize or a typed note linked to an appointment;
  both callers own a transaction, so the push commits or rolls back with the session);
- `Appointments.SetStatusAsync`, after a successful save, when the status becomes `attended`. The push is
  not inside `ApplyAttendedAsync` because that method never saves, and the agenda path has no ambient
  transaction. Undoing "attended" recomputes the patient from the records that remain.

**Recompute.** `RecomputeAsync(db, ct, providerId?, patientId?)` rebuilds the clock from the records, so it
also catches deletions. It runs in the daily job, when the guard years change and at the start of a
discard. Both calls skip discarded patients.

**Advisory lock.** Push and recompute both take
`pg_advisory_xact_lock(hashtextextended('retention:{providerId}', 0))` first and join the caller's
transaction when there is one, so the lock is held until the signature commits. The recompute runs per
provider, one locked transaction each, which keeps the job from overwriting a deadline pushed a moment
earlier.

---

## 4. Endpoints

All under `/api/phi/providers/me` (401 first, provider-scoped, no PHI in URLs, discarded patients left out).

**`GET compliance`**

```json
{
  "unsignedNotes": [
    { "noteId": "...", "patientId": "...", "patientName": "Ana Silva",
      "createdAt": "2026-10-17T15:00:00+00:00", "ageDays": 10 }
  ],
  "retention": [
    { "patientId": "...", "patientName": "Dan Silva", "lastRecordAt": "2020-01-01",
      "retentionUntil": "2025-01-01", "expired": true, "hasFutureAppointment": false }
  ],
  "documents": [
    { "documentId": "...", "patientId": "...", "patientName": "Ana Silva", "kind": "declaracao",
      "status": "signed", "missing": ["finalidade"], "createdAt": "2026-10-22T15:00:00+00:00" }
  ]
}
```

**`GET settings/retention`** returns `{ "guardYears": 5, "unsignedNoteDays": 7 }`.
**`PUT settings/retention`** takes the same body: 204, or 400 `{ errors: { guardYears | unsignedNoteDays } }`
with pt messages.

**`POST patients/{id}/discard`**, **`GET disposal-terms`** and **`GET disposal-terms/{id}/pdf`**: see
sections 8 and 10.

---

## 5. Settings

Guard years 5 to 100 and unsigned-note days 1 to 365. The days go to `Providers.UnsignedNoteDays`. The guard
years are upserted into the `prontuario` rule (the row is written with the default basis), and the
provider's deadlines are recomputed in the same transaction, only when the value changed. Settings changes
are not audited.

---

## 6. Panel blocks

All three lists are ordered by date, ascending, and carry no severity colour.

| Block | Rule |
|---|---|
| Unsigned notes | every note with `SignedAt` null, whatever its status (failed included), whose age is at least `UnsignedNoteDays`. `ageDays` is the practice-local today minus the practice-local creation day. Archived patients are included. Ordered by `createdAt` |
| Retention | `RetentionUntil <= today + 90 days`, which includes deadlines already past. `expired = RetentionUntil < today`, so the deadline day itself is not yet expired. `hasFutureAppointment` is a scheduled or confirmed appointment with `StartsAt > now`. Ordered by `retentionUntil` |
| Documents | not superseded, and either a draft (`status: "draft"`, `missing` from `DocumentConformity.Check` against the draft's ruleset and the current profile; an empty list means it only needs a signature) or a signed document whose latest conformity check did not pass (`status: "signed"`, `missing` from that check). A legacy draft with no ruleset is listed with an empty `missing`. Ordered by `createdAt` |

Document items carry `createdAt`, an addition to the spec contract, because the list is ordered by it.

---

## 7. Daily job and notifications

`RetentionJob` (hosted service) runs at start and then every 24 hours. It is registered inside the existing
`Notifications:WorkerEnabled` block of `Program.cs`, so it is off wherever the worker is off (tests set the
flag to false). `RetentionJobSteps.RunAsync` recomputes the clocks, then, for each provider, inserts one
in-app notification for every unsigned note and every retention item (delivery status `sent`, `SentAt` and
`ScheduledFor` set to the run time).

- **Dedupe.** Each insert uses `ON CONFLICT ("ProviderId","DedupeKey") WHERE "DedupeKey" IS NOT NULL DO
  NOTHING`. Keys are `unsigned:{noteId}` and `retention:{patientId}:{yyyy-MM-dd}` (the deadline date), so a
  moved deadline is a new item, while an item that goes from "within 90 days" to "expired" keeps its key
  and is not notified again.
- **Kinds.** `unsigned_note` with subject type `note` (subject id = the note id) and `retention_due` with
  subject type `patient` (subject id = the patient id).
- **Bell list** (`GET notifications`): the two kinds come back with `patientName` set and `appointmentId`,
  `startsAt` and `exportId` null. Discarded patients are left out.
- **No e-mail,** by decision. The job logs counts and exception types only.

---

## 8. Discard (`Retention/DiscardService`)

`POST patients/{id}/discard` with `{ "confirmName": "Ana Silva" }`.

**Preconditions,** checked in this order, each a 409 `{ message, code }`:

| Code | When |
|---|---|
| `already_discarded` | the patient was discarded already |
| `not_expired` | `RetentionUntil` is not before today, or the record has no clock (no records) |
| `future_appointment` | a scheduled or confirmed appointment with `StartsAt > now` |
| `name_mismatch` | the typed name differs from "First Last" |

The name comparison trims, collapses inner whitespace, strips accents and ignores case; an empty name is a
mismatch. A patient that does not exist or belongs to another provider is a 404. If the term PDF cannot be
uploaded, the answer is 503 `{ code: "storage_unavailable" }` and everything is rolled back.

**Order inside the transaction:** take the provider's retention advisory lock (`RetentionClock.LockAsync`, the
same lock as push and recompute, so every path locks provider first, then patient rows), lock the patient row
`SELECT ... FOR UPDATE`, recompute this patient's
clock (so the decision never rests on a stale clock), run the checks, upload the term PDF, delete the
content, write the term and the audit row, commit.

**Result:** 200 `{ termId, counts: { notes, clinicalDocuments, externalDocuments, sessions, appointments,
transcripts, scaleApplications, medications, diagnoses, treatmentPlans, planObjectives, noteSections,
themeTags, excerptRefs, patientQuotes, followUpItems, tissGuides, auditEntries, shareLinks,
disclosureAuthorizations, documentHashes, extractedFacts, externalDocumentPages, recordingConsents,
intakeResponses, dossiers, insurerAuthorizations, bookingHolds, appointmentSeries, invitations,
notifications, portalAccounts } }`.

**Deleted** (one transaction, children first; the cascades named are the foreign keys' own):

| Table | Rows |
|---|---|
| `Notifications` | patient, note and appointment subjects of the patient (retention, unsigned-note and appointment bell items) |
| `ExcerptRefs` | sources in the patient's notes, external documents and transcripts or sessions |
| `PatientQuotes`, `ThemeTags`, `NoteSections` | the patient's notes |
| `FollowUpItems` | the patient's, or tied to the patient's notes |
| `TissGuides` | the patient's notes or sessions (`Denials`, `DenialFlags` cascade) |
| `AuditEntries` | rows of the patient's notes and documents (see the ruling below) |
| `Transcripts` | the patient's sessions or notes |
| `ShareLinks` | the patient's documents (`ShareAccesses` cascade) |
| `DisclosureAuthorizations`, `DocumentHashes` | the patient's, or tied to the patient's documents |
| `ClinicalDocuments` | the patient's (`ConformityChecks` cascade) |
| `Notes` | the patient's (`BillingCodes` cascade) |
| `MedicationEntries` | the patient's |
| `ExtractedFacts`, `ExternalDocumentPages`, `ExternalDocuments` | the patient's |
| `ScaleApplications`, `PatientDiagnoses` | the patient's |
| `PlanObjectives`, `RecordingConsents` (`ConsentEvents` cascade), `TreatmentPlans` | the patient's |
| `IntakeResponses`, `Dossiers`, `InsurerAuthorizations`, `BookingHolds` | the patient's |
| `Appointments` | all of them (confirmations, tokens, reminders and room sessions cascade); with future active ones ruled out, only past, cancelled and no-show ones remain |
| `AppointmentSeries`, `Sessions` | the patient's (`AudioDestructionLogs` and `SpeechMarkers` cascade) |
| `PatientInvitations` | the patient's |
| `PatientAccounts` | the linked portal account, only when no other provider's patient row still uses it; otherwise only the link is removed |

**Kept:** the `Patients` row (name, creation date and `DiscardedAt`), the `DisposalTerms` row and its PDF,
the `patient_discarded` audit row and the provider-level audit rows (no note or document id, values are ids
only), and `DisposalRecords` (a legacy table the app does not write).

**Patient row after the discard.** Kept: `FirstName`, `LastName`, `CreatedAt`. Set: `DiscardedAt`, and
`ArchivedAt` when it was null, so the existing archive guards block new writes. Cleared: `AccountId`,
`DateOfBirth`, `ContactEmail`, `Phone`, `PreferredLanguage`, `LastVisit`, `LastRecordAt`, `RetentionUntil`.
The patient list (active and archived), detail, update, archive, the medication timeline and attendance
statistics treat a discarded patient as not found, and a live recording refuses to begin for it (the hub
acknowledges `"archived"` for an archived patient and `"not_found"` for a discarded one).

**Audit rows ruling.** The per-note and per-document audit rows of the patient are deleted with the
content. Their foreign keys are `ON DELETE RESTRICT`, so the notes cannot go while they exist, and the rows
hold clinical text (`section_edited` previous and new values, `ai_generated` content). Keeping them would
need the ids nulled and the values scrubbed, which in turn needs every note action allowed without a subject
in `AuditEntries_subject_ck`. What stays is one `patient_discarded` row: `ActorId` is the provider,
`FieldChanged` is `patient`, `NewValue` is `"{patientId} {termId} notes=2,clinicalDocuments=1,..."` (ids and
counts only, including the count of audit rows deleted). The cost if the ruling is wrong is less audit
history.

**Objects, after the commit.** These are deleted with 3 attempts each (100 ms and 200 ms backoff):
note PDFs (`Notes.PdfObjectKey`), clinical document PDFs (`ClinicalDocuments.PdfObjectKey`), external
document objects (`ExternalDocuments.ObjectKey`) and dossier objects (`Dossiers.ObjectKey`). A final failure
logs `Discard: object delete failed for key {Key} ({ErrorType})`, by key and error type only, and never
fails the discard. Export ZIPs are not touched (section 13).

**Retries.** The object keys, the stored flag and a `committing` flag live outside the retried attempt.
The flag is set right before the commit. If a retry then finds the patient discarded and this call's term
row exists, the result is Ok with that term's counts and the object deletes still run. The term PDF is
deleted only when it was uploaded and no commit was ever tried, which covers both a throw and a later
attempt that refused. The term PDF is written to the private bucket before the row writes; if the commit
fails after the upload, the PDF is deleted best-effort.

---

## 9. The database refuses new rows for a discarded patient

The discard holds the patient row `FOR UPDATE`, which blocks anything that takes a lock on that row, but a
concurrent insert could still commit a row for the patient right after the discard. The migration closes
that in the database: function `"Patients_not_discarded"()` runs
`SELECT "DiscardedAt" FROM "Patients" WHERE "Id" = NEW."PatientId" FOR KEY SHARE`, passes a null
`PatientId`, and raises SQLSTATE `ANDIS` ("patient record <id> was discarded") when the date is set.
`BEFORE INSERT ... FOR EACH ROW` triggers named `<Table>_patient_not_discarded` sit on the 18 tables with a
direct `PatientId`: `Appointments`, `AppointmentSeries`, `BookingHolds`, `ClinicalDocuments`,
`DisclosureAuthorizations`, `Dossiers`, `ExternalDocuments`, `FollowUpItems`, `InsurerAuthorizations`,
`IntakeResponses`, `MedicationEntries`, `Notes`, `PatientDiagnoses`, `PatientInvitations`,
`RecordingConsents`, `ScaleApplications`, `Sessions`, `TreatmentPlans`.

An insert that races an open discard waits on the lock, then re-reads the committed row and fails; if the
discard rolls back, the insert goes through. Left out: `DisposalTerms` and `DisposalRecords` (the discard
writes them) and the tables with no `PatientId` of their own (`PlanObjectives`, `ThemeTags`, `ExcerptRefs`,
`NoteSections`, `PatientQuotes`, `Transcripts`), whose parent rows are deleted and cannot be re-created
because their own triggers refuse it.

At the app boundary, `Retention/DiscardedPatient.Is(exception)` walks the inner exceptions looking for a
`PostgresException` with SqlState `ANDIS`, and `DiscardedPatientExceptionHandler` (registered before
`AddProblemDetails`) turns any request that hit the trigger into 404
`{ message: "Este prontuário foi descartado.", code: "discarded" }`.

---

## 10. Disposal term

`GET disposal-terms` returns `[{ id, patientId, patientName, createdAt, periodFrom, periodTo, guardYears,
retentionUntil, basis, counts }]`, oldest first (an addition to the spec, for the list on the page).
`GET disposal-terms/{id}/pdf` returns `{ url, expiresAt }`, a one-hour presigned link with the file name
`termo-descarte-{CODE}.pdf`; 404 for a term that is not the provider's.

The PDF (`DisposalTermPdf`, QuestPDF in the style of `DocumentPdf`: A4, 2 cm, 11 pt, bold centred title,
label and value lines, a footer with provider, CRP and page X of Y) states:

- the discard date and time;
- provider name and CRP, and the patient's name;
- the period of the record (first to last record, over the same kinds as the clock) and the guard years
  with the end date (the term's stored `RetentionUntil`: the patient's own deadline as read before the
  discard cleared it) and the legal basis;
- a table of counts for 11 kinds (notes, psychological documents, external documents, sessions,
  appointments, transcripts, scales, diagnoses, medications, plans, objectives);
- a statement that the discard is irreversible.

The object key is `disposal-terms/{providerId}/{termId}.pdf` (ids only).

---

## 11. Export (F-60, chapter 24)

Discarded patients get no `pacientes/NNN-XX` folder and are not in the index, the counts or the agenda;
folder numbering skips them. When terms exist, `conta/termos-de-descarte/` holds
`AAAA-MM-DD-termo[-N].pdf` (the stored PDF, or a re-render from the row if the object is missing) and
`termos.json` with `[{ id, codigo, descartadoEm, paciente, periodoInicio, periodoFim, anosGuarda, guardaAte,
fundamento, contagens, pdf }]` (`guardaAte` is the term's stored `RetentionUntil`, the same date as "encerrado
em" in the PDF). The index gets a "Termos de descarte" table and the LEIA-ME documents the
folder and the `patient_discarded` audit action. With no discards the export layout is unchanged.

---

## 12. Provider app

- **Page "Conformidade"** (`apps/provider/src/routes/_app/compliance.tsx`, route `/compliance`): the
  Sidenav entry used to point at `/compliance-vault`, which had no route, and now points at `/compliance`
  (the unused `complianceVault` mock flag is gone). Three blocks in the order the server returns them, with
  links to the patient, the note and the document, then the "Termos de descarte" list. Retention dates are
  plain `yyyy-MM-dd`, with no time zone shift. No severity colours, no risk ordering.
- **"Descartar"** shows only on expired rows and is disabled, with the reason, when there is a future
  appointment. The dialog (`DiscardDialog`) states irreversibility, lists the **categories** that will be
  deleted and what is kept (the name and the term), and asks for the patient's name. It does not show
  counts: the server knows them only after the discard, and the term carries them. Specific copy covers
  `already_discarded`, `not_expired`, `future_appointment`, `name_mismatch` and `storage_unavailable`, with
  a generic fallback. On success it invalidates `compliance`, `patients`, `disposal-terms` and
  `notifications` (and `compliance` also on `not_expired` and `future_appointment`).
- **Term PDF** (`DisposalTermsList`): the tab is opened synchronously on click, then pointed at the link
  (the same pattern as the external-document rows), with a message if the pop-up is blocked.
- **Settings, "Guarda"** (`RetentionSettingsSection`): guard years (5 or more) and unsigned-note days
  (1 or more), with server field errors shown. Saving invalidates `compliance` and `retention-settings`.
- **Bell** (`NotificationBell`): `unsigned_note` and `retention_due` have their own copy and open
  `/compliance`.
- API client `packages/shared/src/api/compliance.ts`; copy under `compliance.*`, `settings.retention.*`
  and `notifications.unsignedNote` / `retentionDue` in `pt.json`.

---

## 13. Limits and known gaps

- **Export ZIPs made before a discard keep the record** until they expire (`ExportSweep`), and so does any
  copy the provider downloaded; the app cannot reach those. Exports made after the discard show the record
  only through its term.
- **The Keycloak user** of a patient portal account is not touched; only the `PatientAccounts` row goes.
- **The guard rule per record kind** is not used beyond `prontuario`.
- **The per-note and per-document audit rows are deleted** (section 8).
- **No e-mail alerts,** by decision.
- **The bell unread count** counts every in-app row; the discard deletes the patient's own items, so it
  stays right.
- **Write paths that do not check archive state** could still reach a discarded patient by id in the
  application; the database trigger covers inserts on the 18 tables, not updates.
- **Not exercised in a browser:** the page, dialog and settings section are type-checked and built only.

---

## 14. Privacy rules

Logs, notification rows, dedupe keys, audit values and URLs carry ids, dates, kinds and counts, never names
or clinical text. The patient name travels only in the POST body of the discard. Everything is
provider-scoped (a foreign id answers 404), and discarded patients are left out of every list.

---

## 15. Tests

| Class | Covers |
|---|---|
| `Retention/RetentionClockTests` (scratch DB) | record kinds, attended appointments, push versus recompute, leap day, correction of a document, guard years change, a push in flight versus a recompute |
| `Retention/ComplianceQueryTests` | scoping, 90-day window, unsigned threshold, ordering, documents block |
| `Retention/RetentionJobTests` | one notification per item, not repeated, moved deadline |
| `Retention/DiscardServiceTests` | blocked before expiry, with a future appointment, wrong name; deletes everything including objects; term; audit without PHI; already discarded; the insert trigger and the races |
| `Exports/AccountExportDisposalTermTests` | terms in the export, discarded patients left out |
| `Data/RetentionPanelMigrationTests` | migration, constraints, the 18 triggers |

The retention and discard tests run on a scratch database bootstrapped from `anamnys-db-script.sql`
(`Tests/Retention/RetentionScratch.cs`). The other fixture classes use the dev database, which needs the
migration applied, and need Aspire stopped.

---

## 16. Where things are

| Piece | Path |
|---|---|
| Server | `anamnys-aspire.Server/Retention/` (clock, compliance query, settings, job, discard, term PDF, endpoints) |
| Migration | `design/migrations/2026-10-28-retention-panel.sql` |
| Tests | `anamnys-aspire.Tests/Retention/`, `Tests/Exports/AccountExportDisposalTermTests.cs`, `Tests/Data/RetentionPanelMigrationTests.cs` |
| Page and dialog | `apps/provider/src/routes/_app/compliance.tsx`, `apps/provider/src/components/compliance/` |
| Settings section | `apps/provider/src/components/settings/RetentionSettingsSection.tsx` |
| API client | `packages/shared/src/api/compliance.ts` |

## Dev seed

`dev-seed/retention-samples.sql` creates two fictitious patients per non-test provider: Rodrigo Teixeira (one signed note on 2020-03-10, retention ended 2025-03-10, can be discarded) and Marina Coelho (signed note on 2021-12-15, retention ends 2026-12-15, inside the 90-day window, plus a note ready for review left unsigned for 15 days). Every note has DAP sections. Never run outside a local database.
