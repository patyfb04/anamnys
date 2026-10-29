# Retention and Compliance Panel (F-06)

**Date:** 2026-10-28
**Status:** Approved for implementation
**Scope:** Backlog F-06. A retention clock per patient record, a "Conformidade" page listing unsigned notes,
records near or past the end of retention and issued documents with compliance pending, daily bell
notifications, and an irreversible discard with a disposal term.

Done when (backlog): the provider sees, on one screen, every unsigned note and every record approaching the
retention limit.

## 1. Decisions (agreed with the user, 2026-10-27)

| Topic | Decision |
|---|---|
| Clock | One deadline per patient record: date of the last record (signed note, issued document or session) plus the guard years. Every new record pushes it. Default 5 years (Res. CFP 001/2009), configurable upward in Settings, never below 5 (`RetentionRules_GuardYears_ck`). |
| Discard | From the panel, only for an expired record with no future appointment; confirmed by typing the patient's name. Generates a disposal term (PDF) and deletes the clinical content; keeps the term, a minimal patient row marked discarded, and the audit trail. Irreversible. |
| Alerts | Daily job creates one in-app bell notification per new item in blocks 1 and 2; never repeated for the same item. No e-mail. |
| Language | Lists with dates, ordered by date; no severity colours, no ordering by risk. |

## 2. Data

Migration `design/migrations/2026-10-28-retention-panel.sql` (idempotent, mirrored in
`anamnys-db-script.sql`, guards like the earlier migrations for `AuditEntries_subject_ck`):

- `Patients`: `"LastRecordAt" date`, `"RetentionUntil" date`, `"DiscardedAt" timestamptz`; index on
  (`ProviderId`, `RetentionUntil`).
- `RetentionRules`: one row per provider with `RecordKind = 'prontuario'` used for the clock (absent =
  5 years, `Res. CFP 001/2009`).
- `Providers` (or the existing settings store): `"UnsignedNoteDays" integer NOT NULL DEFAULT 7 CHECK (>= 1)`.
- `DisposalTerms`: `Id`, `ProviderId`, `PatientId`, `CreatedAt`, `ObjectKey` (PDF in the private bucket, ids
  only), `PeriodFrom`, `PeriodTo`, `GuardYears`, `Basis`, `Counts jsonb`.
- `Notifications` reuse for the bell; a dedupe key per item (`retention:{patientId}:{retentionUntil}`,
  `unsigned:{noteId}`) so the job never repeats.

## 3. Server

- `Retention/RetentionClock`: recompute `LastRecordAt`/`RetentionUntil` for a patient (hooked into note
  signing, document signing, session creation) and for all patients in the daily job.
- `GET providers/me/compliance` → `{ unsignedNotes: [{ noteId, patientId, patientName, createdAt, ageDays }],
  retention: [{ patientId, patientName, lastRecordAt, retentionUntil, expired, hasFutureAppointment }]
  (within 90 days or expired), documents: [{ documentId, patientId, patientName, kind, status, missing }] }`,
  ordered by date; provider scoping; discarded patients excluded.
- `GET/PUT providers/me/settings/retention` → `{ guardYears (>= 5), unsignedNoteDays (>= 1) }`; changing
  guard years recomputes the provider's deadlines.
- `POST providers/me/patients/{id}/discard { confirmName }` → 409 `not_expired` / `future_appointment` /
  `name_mismatch` / `already_discarded`; otherwise in one transaction: build the term (QuestPDF, existing
  PDF style), upload it, delete notes, sections, clinical documents (and their PDFs), external documents
  (objects, pages, facts), transcripts, excerpt refs, theme tags, scale applications, medications,
  diagnoses, plan objectives, appointments in the past, invitations/portal account; keep the patient row
  with name and `DiscardedAt`, the term and audit (`patient_discarded`, ids and counts only). Object deletes
  happen after commit with retries; a failed object delete is logged by key, never blocks.
- `GET providers/me/disposal-terms/{id}/pdf` (signed link like other PDFs).
- Daily job (existing scheduler pattern): recompute, then notify new items.
- Export (F-60): disposal terms included; discarded patients appear only through their term.

## 4. Provider app

- Page "Conformidade" (existing menu entry): three blocks (unsigned notes, retention, documents), links to
  the note/patient/document; "Descartar" opens a dialog explaining irreversibility, showing counts, asking
  for the patient name; after discard the term link appears in a "Termos de descarte" list on the page.
- Settings: "Guarda" section with guard years and unsigned-note days.
- Bell notifications open the page.
- Copy in pt.json; hover classes.

## 5. Documentation

Chapter `documentation/30-retention-panel.md`; index; status (F-06 done); gaps; Notion.

## 6. Testing

- Clock: last record kinds, push on new record, guard years change.
- Panel: scoping, 90-day window, unsigned threshold, ordering.
- Discard: blocked before expiry / with future appointment / wrong name; deletes everything incl. objects;
  term generated; audit without PHI; idempotency (already_discarded).
- Notifications: one per item, not repeated.
- Export includes the term.
- Frontend: build + lint; manual check.
