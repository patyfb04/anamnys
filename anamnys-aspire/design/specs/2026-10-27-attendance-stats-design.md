# Attendance Statistics (F-04)

**Date:** 2026-10-27
**Status:** Approved for implementation
**Scope:** Backlog F-04. Session metadata is already captured (appointment status, cancellation time and
author; session times). This delivery computes attendance and interval statistics per patient, shows them
in a "Comparecimento" card on the patient record and adds a "Consultas" lane to the F-09 timeline.

Done when (backlog): attendance and interval statistics can be computed per patient.

## 1. Decisions (agreed with the user, 2026-10-27)

| Topic | Decision |
|---|---|
| Where | Card "Comparecimento" on the patient record, collapsible like the other cards, period 6 m / 1 y / all. The Análises page (F-10) reuses the same computation later. |
| What counts | The appointment is the base. Sessions with no appointment (live session or note without one) count as attended, marked "sem agendamento". A session linked to an appointment is not counted twice. Past appointments still `scheduled`/`confirmed` count as "sem registro de comparecimento", neither attended nor missed. Future appointments are excluded. |
| Display | Numbers card plus a "Consultas" lane at the top of the "Linha do tempo" card: one mark per appointment/session, a different shape per status, one neutral colour, legend, click shows date and status. No duration line. |
| Language | Observation only: counts and dates with neutral labels; no value colours, no evaluative wording ("falta muito"), no ordering by severity. |

## 2. Data

No migration. Sources: `Appointments` (`StartsAt`, `Status`, `CancelledAt`, `CancelledBy`, `SessionId`) and
`Sessions` (`ActualStart`, `CreatedAt`, `Attendance`) for sessions with no appointment pointing to them.
Dates are the practice-local day (`PatientSearchQuery.PracticeToday`), as in the F-09 timeline.

## 3. Server

- `Attendance/AttendanceStats` (pure): from a list of events `{ on: DateOnly, at: DateTimeOffset, kind:
  attended | attended_unscheduled | no_show | cancelled_provider | cancelled_patient | unrecorded,
  cancelledAt? }` computes:
  - counts per kind (cancellations by `system` count with the provider's);
  - cancellation lead in hours (`StartsAt − CancelledAt`, negative clamped to 0): median, min, max, count;
  - intervals in days between consecutive attended events (both attended kinds): median, min, max, last;
  - days since the last attended event (relative to the practice-local today; independent of `from`, up to `to`);
  - an appointment still scheduled/confirmed whose linked session has `Attendance = attended` counts as attended;
  - every statistic is `null` when there is not enough data (no cancellation, fewer than two attended).
- `GET providers/me/patients/{id}/attendance?from=&to=` (dates only, no PHI; provider scoping; 404 for
  another provider's patient; from > to → validation problem) → `{ counts: { attended,
  attendedUnscheduled, noShow, cancelledByProvider, cancelledByPatient, unrecorded }, cancellationLeadHours:
  { median, min, max, count } | null, intervalDays: { median, min, max, last } | null, daysSinceLast }`.
  Only events up to now; the period filters on the practice-local day.
- Timeline (`GET …/patients/{id}/timeline`) gains `appointments: [{ id, on, kind }]` with the same kinds
  and period rules (past only).

## 4. Provider app

- Card "Comparecimento" before "Linha do tempo", `collapsibleId="attendance"`, period 6 m / 1 y / all
  (default 1 y): counts, cancellation lead (median, range), interval (median, range, last), days since the
  last session; "—" when null; empty state when there is nothing in the period.
- Timeline: "Consultas" lane above the medications, a shape per kind (filled circle attended, ringed
  circle attended without appointment, open circle no-show, × cancelled by provider, × with ring cancelled
  by patient, dash unrecorded), one neutral colour, legend; click details show date and status.
- Query invalidation: appointment status changes and note/live-session creation invalidate
  `["attendance", patientId]` and `["timeline", patientId]` where the patient id is known.
- Copy in pt.json; hover classes.

## 5. Documentation

Chapter `documentation/29-attendance-stats.md`; index; status (F-04 done); gaps; Notion.

## 6. Testing

- Pure: medians with odd/even counts; no or one attended (null intervals); unscheduled sessions; unrecorded
  past appointments; future appointments excluded; cancellation after start (lead 0); system
  cancellations.
- Service (real DB): scoping; period; session linked to an appointment counted once; timeline lane.
- Frontend: build + lint; manual check on seeded patients.
