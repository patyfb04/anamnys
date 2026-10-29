# Chapter 29: Attendance Statistics (F-04)

How attendance and interval statistics are computed per patient from appointments and sessions, shown in
the "Comparecimento" card on the patient record and as a "Consultas" lane on the "Linha do tempo" card.
Last checked against the code on 2026-10-27 (branch `feature/attendance-stats`).
Design: `design/specs/2026-10-27-attendance-stats-design.md`.

What a future change has to respect: the appointment is the counting base and a session is never counted
twice (section 3), the language is observation only, with no evaluative wording or value colours
(section 6), and the requests carry dates only, never PHI (section 4).

---

## 1. Purpose

Done when (backlog): attendance and interval statistics can be computed per patient. Session metadata
was already stored (appointment status, cancellation time and author, session times); this delivery turns
it into counts, cancellation lead times, intervals between sessions and days since the last one. The
Análises page (F-10) is expected to reuse the same computation.

---

## 2. Sources

No migration. Everything is read from existing columns:

| Table | Columns used |
|---|---|
| `Appointments` | `StartsAt`, `Status`, `CancelledAt`, `CancelledBy`, `SessionId` |
| `Sessions` | `ActualStart`, `CreatedAt`, `Attendance`, only for sessions that no appointment points to |

Days are the practice-local day (`PatientSearchQuery.PracticeToday`, America/Sao_Paulo), as in the F-09
timeline.

---

## 3. Counting rules

`Attendance/AttendanceQuery` turns the rows into events and `Attendance/AttendanceStats` (pure) computes
the statistics from them.

- **The appointment is the base.** Each past appointment becomes one event: `attended` -> attended;
  `no_show` -> no-show; `cancelled` -> cancelled by patient when `CancelledBy` is `patient`, otherwise
  cancelled by provider; `scheduled` or `confirmed` -> unrecorded ("sem registro de comparecimento").
- **System cancellations** count with the provider's.
- **Future appointments are excluded** (`StartsAt` after now), in the query and again in the pure
  computation.
- **A session linked to an appointment is counted once**, through the appointment. Only sessions that no
  appointment references are considered.
- **Unscheduled sessions** (a live session or note without an appointment) count as attended without
  appointment only when `Attendance` is `attended`. A session marked `scheduled` with no appointment is a
  plan and is ignored. The time used is `ActualStart`, else `CreatedAt`.
- **Unrecorded** past appointments are neither attended nor missed.
- **Period.** The optional `from` and `to` filter on the practice-local day. Counts and intervals are
  computed within the filtered events; days since the last session ignore `from` (last attended event up to `to`).
- **Cancellation lead** is `StartsAt - CancelledAt` in hours; a cancellation after the start is clamped
  to 0. A cancellation with no `CancelledAt` is counted but skipped in the lead statistics. Median, min
  and max are hours as numbers.
- **Intervals** are the days between consecutive attended events, both attended kinds together. Two
  attended events on the same day give a 0-day interval. The median is a number (mean of the two middle
  values when the count is even); min, max and last are whole days. `last` is the interval ending at the
  most recent attended event.
- **Days since last** is counted from the last attended event (whatever `from` is, up to `to` and now) to the practice-local today.
- **Linked session.** An appointment still `scheduled`/`confirmed` whose linked session is `attended` counts as attended.
- Every statistic is `null` when there is not enough data: no cancellation with a time, fewer than two
  attended events, no attended event.

---

## 4. Endpoints

Both under `/api/phi/providers/me` (401 first, provider-scoped, dates only, no PHI in URLs).

**`GET patients/{patientId}/attendance?from=yyyy-MM-dd&to=yyyy-MM-dd`**

```json
{ "counts": { "attended": 8, "attendedUnscheduled": 1, "noShow": 1, "cancelledByProvider": 0,
              "cancelledByPatient": 2, "unrecorded": 1 },
  "cancellationLeadHours": { "median": 15, "min": 6, "max": 24, "count": 2 },
  "intervalDays": { "median": 7, "min": 7, "max": 14, "last": 7 },
  "daysSinceLast": 3 }
```

Both dates are optional. 400 validation problem when `from` is after `to`; 404 for an unknown or foreign
patient. `cancellationLeadHours`, `intervalDays` and `daysSinceLast` are `null` when there is no data.

**`GET patients/{patientId}/timeline`** (chapter 28) now also returns the events of the lane, with the
same period rules and past events only, ordered by start then id:

```json
"appointments": [ { "id": "...", "on": "2026-09-10", "kind": "attended" },
                  { "id": "...", "on": "2026-10-01", "kind": "attended_unscheduled" } ]
```

`kind` is one of `attended`, `attended_unscheduled`, `no_show`, `cancelled_provider`, `cancelled_patient`,
`unrecorded`.

---

## 5. Timeline lane

"Consultas" is the top lane of the "Linha do tempo" card, above the medications, on the same date axis.
One mark per appointment or session, a different shape per kind and one neutral colour:

| Kind | Shape |
|---|---|
| attended | filled circle |
| attended without appointment | ringed circle |
| no-show | open circle |
| cancelled by provider | x |
| cancelled by patient | x with ring |
| unrecorded | dash |

A legend sits under the chart. Clicking a mark opens the inline details panel with the date and the
status. There is no duration line.

---

## 6. Provider app (card)

`apps/provider/src/components/attendance/AttendanceCard` (with `AttendanceMark` for the shapes), placed
before the "Linha do tempo" card on the patient record, `collapsibleId="attendance"`, so it collapses like
the other cards.

- Period buttons 6 meses, 1 ano (default) and Tudo; the period is sent as dates only.
- Shows the counts, the cancellation lead (median and range), the interval (median, range, last) and the
  days since the last session. A null value shows "—"; a period with nothing in it shows an empty state.
- Observation only: counts and dates with neutral labels, no value colours, no evaluative wording, no
  ordering by severity.
- API client `packages/shared/src/api/attendance.ts`; copy under `attendance.*` and `timeline.*` in
  `pt.json`.

---

## 7. Query invalidation

The keys are `["attendance", patientId]` and `["timeline", patientId]`.

- Creating a note and finishing a live session invalidate both with the patient id.
- Appointment create, update and status change (`useAppointments`) invalidate `["attendance"]` and
  `["timeline"]` by prefix, because the hook does not know the patient id.
- The refresh on the patient record also invalidates the attendance query.

---

## 8. Tests

| Class | Covers |
|---|---|
| `Attendance/AttendanceStatsTests` (pure, 8) | medians with odd and even counts, no or one attended (null intervals), unscheduled sessions, unrecorded past appointments, future excluded, cancellation after start (lead 0), system cancellations |
| `Attendance/AttendanceQueryTests` (real DB, 5) | mix with a linked session counted once, unrecorded, future excluded and a system cancel; `ActualStart` to `CreatedAt` fallback; period on the practice-local day; scoping with no patient mixing; the timeline lane with its period and future excluded |

The pure tests passed (8/8). The real-database class and the existing timeline tests were compiled but
not run, because the fixture classes need Aspire stopped. The provider app was checked with build and lint
only.

---

## 9. Limits and known gaps

- No duration line; the statistics use dates, not session length.
- Appointment changes invalidate the attendance and timeline queries by prefix, so the cache of every
  patient is refreshed, not only the one concerned.
- A session marked `scheduled` with no appointment is ignored.
- No practice-wide view: the card is per patient and the aggregation waits for F-10.
- Not exercised in a browser: the card, the lane and the details are type-checked and built only.

---

## 10. Where things are

| Piece | Path |
|---|---|
| Server | `anamnys-aspire.Server/Attendance/`, `anamnys-aspire.Server/Medications/MedicationTimeline.cs` |
| Tests | `anamnys-aspire.Tests/Attendance/` |
| Card | `apps/provider/src/components/attendance/` |
| Timeline lane | `apps/provider/src/components/timeline/` |
| API client | `packages/shared/src/api/attendance.ts` |
