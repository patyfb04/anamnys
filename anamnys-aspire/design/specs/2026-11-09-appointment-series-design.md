# Recurring Appointments (F-29, delivery 3 of 4)

**Date:** 2026-11-09
**Status:** Approved for implementation
**Scope:** Backlog F-29, recurring care as a series: weekly is the norm in psychotherapy. Per-occurrence divergence, so
rescheduling one week does not touch the others. Earlier deliveries: 1 (create, reschedule, statuses, day/week, time
zone), 2 (availability, holidays, blocks, month view). Delivery 4: optional Google Calendar mirror.

Done when (backlog, this part): the provider books a recurring appointment and reschedules a single occurrence, and
nothing requires a Google account.

Out of scope: monthly or free-form recurrence (gap); moving a series to another patient.

## 1. Decisions (agreed with the user, 2026-11-09)

| Topic | Decision |
|---|---|
| Occurrences | Real `Appointments` rows, generated ahead for the next 12 weeks and extended daily while the series is active. Every existing flow (statuses, confirmations, reminders, attendance, availability, blocks) works unchanged. |
| Patterns | Weekly and every 2 weeks; ends never, on a date, or after N sessions. Weekday and time come from the first appointment. |
| "This and following" | The series is split: the old one ends before the chosen occurrence, a new one starts there with the new day/time/duration/modality. Future occurrences never individually rescheduled and not confirmed move; individually rescheduled or confirmed ones stay, and the UI lists them before saving. |
| Notices | One summary e-mail when the series is created; each occurrence's confirmation request is scheduled 48 h before it (or immediately if closer); the 1 h reminder is unchanged; generation by the daily extension sends no e-mail; ending the series sends one notice. |

## 2. Data

Migration `design/migrations/2026-11-10-appointment-series.sql` (idempotent, bootstrap, migration-test chains after
`2026-11-09-availability.sql`). `AppointmentSeries` (schema only today: `Id`, `PatientId`, `ProviderId`,
`RecurrenceRule`, `StartsOn`, `EndsOn`, `DefaultMinutes`, `DefaultModality`, `CancelledAt`) gains:

| Column | Notes |
|---|---|
| `Interval` integer NOT NULL default 1 | 1 weekly, 2 every two weeks (check 1..2) |
| `Weekday` integer NOT NULL | 0–6, 0 = Sunday, from the first occurrence's local date |
| `StartTime` time NOT NULL | local time of the first occurrence |
| `Timezone` text NOT NULL default `America/Sao_Paulo` | |
| `OccurrenceCount` integer | "after N sessions" (1..520); null when the end is a date or never |
| `GeneratedUntil` date | last local date generation has covered |
| `PreviousSeriesId` uuid | FK `AppointmentSeries`, the series this one was split from |
| `CreatedAt` timestamptz NOT NULL default now() | |

Checks: `EndsOn` and `OccurrenceCount` not both set; `EndsOn >= StartsOn`. FKs to `Providers` and `Patients` if
missing. `RecurrenceRule` stays as a readable description (e.g. `WEEKLY;INTERVAL=2`), written by the server.

`Appointments` gains `SeriesIndex` integer (1-based occurrence number, null for single appointments) and `Detached`
boolean NOT NULL default false (true once the occurrence was rescheduled on its own). Unique index on
(`SeriesId`, `SeriesIndex`) where `SeriesId` is not null. Index (`SeriesId`, `StartsAt`).

Audit actions `series_created`, `series_split`, `series_ended`. Notification/reminder template for the series summary
e-mail (`series_scheduled`) and the series ended notice (`series_ended`), added wherever templates are enumerated.

## 3. Rules

- **Occurrence dates.** Occurrence k (1-based) falls on `StartsOn + (k − 1) × 7 × Interval` days; the instant is that
  local date at `StartTime` in `Timezone` (DST-safe: the local time is kept). Duration `DefaultMinutes`, modality
  `DefaultModality`.
- **Generation** (`SeriesGenerator.ExtendAsync(db, series, today, ct)`): creates the missing occurrences with local
  date ≤ `today + 84 days`, stopping at `EndsOn` or `OccurrenceCount`, skipping indexes that already exist (idempotent
  through the unique index), and sets `GeneratedUntil`. New occurrences have status `scheduled`, `CreatedBy` provider,
  and get their notices per §3 Notices. Occurrences falling on blocks or holidays are still created (the calendar shows
  them as outside availability); generation never cancels anything.
- **Daily extension** (`SeriesExtensionJob`, a `BackgroundService` like `RetentionJob`): once a day, for every series
  with `CancelledAt` null and not ended, call `ExtendAsync`. Logs counts only.
- **Single occurrence.** Rescheduling an occurrence through the existing appointment route sets `Detached = true`.
  Cancelling one occurrence uses the existing route; the series continues.
- **Split** ("this and following" from occurrence k with new weekday/time/duration/modality): in one transaction the
  old series gets `EndsOn` = the local date before occurrence k (and `OccurrenceCount` cleared); a new series is
  created starting at the new first date (the new weekday on or after occurrence k's date, at the new time), with the
  remaining count when the old series ended after N sessions (N − (k − 1)), the same `EndsOn` otherwise, and
  `PreviousSeriesId` = old. Old future occurrences from k on that are not `Detached` and not `confirmed` and still
  `scheduled` are deleted and regenerated by the new series (their indexes restart at 1 in the new series); the
  `Detached` or `confirmed` ones stay, keep their time, and are re-linked to the new series with `Detached = true`;
  so are the cancelled, attended and no-show ones from k, so the new series never generates their week again. A
  re-linked occurrence takes index `oldIndex − (k − 1)` in the new series.
  The patient gets **one** summary notice of the new pattern (§3 Notices), not one per moved occurrence.
- **End** (from date D): sets `EndsOn` = D − 1 (or `CancelledAt` when D is the first occurrence's date), cancels the
  future active occurrences on or after D through the provider cancellation path without per-occurrence e-mails, and
  sends one "series ended" notice. Occurrences before D are untouched.
- **Availability check** on create applies to the first occurrence only (the existing 409 `outside_availability` and
  acknowledgement); split applies it to the new first occurrence.
- **Notices:**
  - series created: one summary e-mail ("Sessões marcadas: toda terça às 14:00, a partir de 17/11, 10 sessões") to
    patients with an e-mail; no per-occurrence "scheduled" e-mail;
  - each occurrence (created at series creation, by the extension or by a split) gets its confirmation request
    scheduled for `StartsAt − max(48 h, AutoCancelHours + 24 h)` of the provider's booking policy (48 h when
    auto-cancel is off), or now if closer, instead of immediately, and the existing 1 h reminder; the confirmation
    deadline rules apply from the actual send time, so an auto-cancel deadline always falls after the request and
    before the session in both modes;
  - the summary e-mail states the sessions actually created and, when a slot was taken, the dates left out (for a
    series ending after N sessions, "Foram marcadas M das N sessões");
  - an occurrence the daily extension cannot create because its slot is taken becomes one in-app bell notice
    (`series_occurrence_skipped`, subject the series, once per series and index);
  - split: one summary e-mail of the new pattern;
  - end: one "series ended" e-mail with the end date;
  - single-occurrence reschedule or cancel: the existing per-appointment notices.

## 4. Endpoints

All under `/api/phi/providers/me`, provider-scoped, 401 first, foreign ids 404, `no-store`.

| Route | Answer |
|---|---|
| `POST appointment-series` | body `{ patientId, startsAt, durationMinutes, modality, interval (1\|2), endsOn?, occurrenceCount?, acknowledgeOutsideAvailability? }`; 201 `{ seriesId, firstAppointmentId, generated, skipped: [ { seriesIndex, startsAt } ] }`; 400 `invalid_interval`, `invalid_end`; 409 `outside_availability` as for appointments; writes `series_created` |
| `GET appointment-series/{id}` | `{ id, patientId, interval, weekday, startTime, timezone, startsOn, endsOn, occurrenceCount, active, description, upcoming: [ { id, startsAt, status, seriesIndex, detached } ] }` |
| `GET patients/{patientId}/appointment-series` | the patient's active series, same shape without `upcoming` |
| `POST appointment-series/{id}/split/preview` | body `{ fromAppointmentId, weekday, startTime, durationMinutes, modality }`; `{ newFirstStartsAt, moving: n, staying: [ { id, startsAt, reason: detached\|confirmed } ] }` |
| `POST appointment-series/{id}/split` | same body + `acknowledgeOutsideAvailability?`; 200 `{ newSeriesId, skipped: [ { seriesIndex, startsAt } ] }`; writes `series_split` |
| `POST appointment-series/{id}/end` | body `{ fromDate }`; 200 `{ cancelled: n }`; writes `series_ended` |

The appointment list and detail responses gain `seriesId`, `seriesIndex`, `detached` and, when in a series,
`seriesDescription`.

## 5. Provider UI

- New appointment form: "Repetir" (Não repete / Toda semana / A cada 2 semanas); when repeating, "Termina" (Nunca /
  Em [data] / Após [N] sessões) and a summary line. The availability warning applies to the first date.
- Calendar cards (day, week, month): a small repeat icon for series occurrences.
- Appointment details of an occurrence: "Faz parte de uma série: toda terça às 14:00 (sessão 4 de 10)" ("sessão 4"
  without an end); "Remarcar" asks "Só esta sessão" or "Esta e as seguintes" (the latter shows the split preview list
  before saving); "Cancelar" asks "Só esta sessão" or "Encerrar a série a partir desta".
- Patient page: the patient's active series with "Encerrar série".
- Copy in `pt.json` under `series.*`.

## 6. Testing

- Generation: weekly and every two weeks; end by date, by count, never; 12-week horizon; daily extension idempotent;
  DST keeps local time; occurrences on blocks/holidays created.
- Single: reschedule sets `Detached`; cancel leaves the others.
- Split: series linked; only untouched scheduled occurrences regenerated; detached and confirmed stay, re-linked,
  listed by the preview; remaining count carried for count-ended series; availability check on the new first date.
- End: future active occurrences from D cancelled, earlier untouched, one notice, no per-occurrence e-mails.
- Notices: one summary on creation; confirmations scheduled at `StartsAt − 48 h` (or now); none on extension.
- Integration: a block with "cancelar e avisar" cancels series occurrences like any appointment.
- Scoping, 401, migration and chains. Front: provider build and lint.

## 7. Documentation

- Chapter `documentation/42-recurring-appointments.md`; index; status (F-29 delivery 3); gaps (monthly/free-form
  recurrence; moving a series to another patient); Confluence pending.
