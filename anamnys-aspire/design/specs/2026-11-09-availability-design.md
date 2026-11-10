# Availability, Blocks and Month View (F-29, delivery 2 of 4)

**Date:** 2026-11-09
**Status:** Approved for implementation
**Scope:** Backlog F-29, the part about the provider publishing when they work and blocking absences, plus the month
view. Delivery 1 (create, reschedule, statuses, day/week view, time zone per appointment) exists. Later deliveries:
recurring appointments (3) and the optional Google Calendar mirror (4). Booking by the patient with free slots is F-62
(patient portal, second phase).

Done when (backlog, this delivery's part): the provider defines weekly availability and blocks a week of vacation, and
nothing requires a Google account.

## 1. Decisions (agreed with the user, 2026-11-09)

| Topic | Decision |
|---|---|
| Split of F-29 | Three deliveries: availability + blocks + month view; recurrence; Google mirror. |
| Booking outside availability | Warn and allow: the provider sees why ("fora da sua disponibilidade", "cai nas suas férias de 10/01 a 20/01", "feriado: Tiradentes") and confirms with "Marcar mesmo assim". Availability is a guide for the provider; it becomes a hard rule only for patient booking (F-62). |
| Holidays | National holidays computed by the server (fixed dates and the Easter-based ones); the provider chooses whether they work on holidays (default: no) and can switch individual holidays off; local holidays are blocks of kind `holiday`. No external service. |
| Block over booked appointments | Before saving, the affected appointments are listed; the provider chooses "Manter as consultas" or "Cancelar e avisar os pacientes" (existing cancellation notice, reason "ausência do profissional"). Nothing is cancelled without that explicit choice. |
| Where | Settings, new section "Horários de atendimento" (weekly hours, effective date, national holiday preferences); Agenda: "Bloquear período" and the upcoming blocks list. |

## 2. Data

Existing tables (schema only today):

- `AvailabilityRules` (`ProviderId`, `Weekday` 0–6 with 0 = Sunday, `StartsAtTime`, `EndsAtTime`, `SlotMinutes`,
  `EffectiveFrom`, `EffectiveTo`, `Timezone`). One row per interval per weekday per version. `SlotMinutes` is not used
  in this delivery (F-62).
- `AvailabilityExceptions` (`ProviderId`, `StartsAt`, `EndsAt`, `AllDay`, `Kind` `block|vacation|holiday`, `Reason`).

Migration `design/migrations/2026-11-09-availability.sql` (idempotent, bootstrap, migration-test chains):

- `ProviderHolidaySettings` (`ProviderId` PK/FK `Providers`, `WorksOnHolidays` boolean default false,
  `DisabledHolidays` text[] default `{}` — holiday keys such as `tiradentes`, `carnaval_segunda`).
- Foreign keys from `AvailabilityRules` and `AvailabilityExceptions` to `Providers` if missing; indexes
  (`ProviderId`, `EffectiveFrom`) and (`ProviderId`, `StartsAt`).
- Audit actions `availability_changed`, `block_created`, `block_deleted`, `appointment_outside_availability`.
- `Reason` max 200 characters (check).

## 3. Rules

- **Versioning.** Saving new hours "a partir de D" closes every rule version that is open on D (`EffectiveTo = D - 1`)
  and deletes versions that start after D (a future version replaced by a newer decision); the new rows start on D
  with `EffectiveTo` null. Nothing before D changes. D cannot be in the past (provider's local date).
- **Intervals.** Within a weekday of one version: start < end, no overlap, at most 6 intervals, times on 5-minute
  steps. "Não atendo" = no rows for that weekday.
- **Time zone.** Rules are in the provider's time zone (`America/Sao_Paulo` today, the value stored per row);
  converting a rule to instants for a date uses that zone (DST-safe).
- **National holidays** (`NationalHolidays.For(year)`): fixed — 01/01 Confraternização Universal, 21/04 Tiradentes,
  01/05 Dia do Trabalho, 07/09 Independência, 12/10 Nossa Senhora Aparecida, 02/11 Finados, 15/11 Proclamação da
  República, 20/11 Dia Nacional de Zumbi e da Consciência Negra, 25/12 Natal; moving (from Easter, computed with the
  anonymous Gregorian algorithm) — Carnaval segunda (Easter − 48) and terça (Easter − 47), Sexta-feira da Paixão
  (Easter − 2), Corpus Christi (Easter + 60). Each has a stable key and a Portuguese name. A holiday applies when the
  provider does not work on holidays and the key is not in `DisabledHolidays`.
- **Overlay** (`AvailabilityOverlay.ForRange(providerId, from, to)`): per local date in the range, the working windows
  from the version in force on that date, minus nothing (windows are not cut by blocks — blocks and holidays are
  returned separately so the UI can draw both), the blocks overlapping the range, and the applicable national holidays.
- **Outside-availability check** (used on create and reschedule): an appointment is outside when any part of it falls
  outside the day's windows, or it overlaps a block, or its date is an applicable national holiday. The reasons are
  returned (kind, dates, holiday name). Status changes never run the check.

## 4. Endpoints

All under `/api/phi`, provider-scoped, 401 first, unknown or foreign ids 404, `no-store`.

| Route | Answer |
|---|---|
| `GET providers/me/availability` | `{ current: { effectiveFrom, days: [ { weekday, intervals: [ { start, end } ] } ] }, upcoming: [ same shape ] }` |
| `PUT providers/me/availability` | body `{ effectiveFrom, days }`; 400 `invalid_intervals`, `effective_in_past`; writes `availability_changed` |
| `DELETE providers/me/availability/upcoming/{effectiveFrom}` | cancels a future version (reopens the previous one) |
| `GET`/`PUT providers/me/holiday-settings` | `{ worksOnHolidays, disabledHolidays }`; GET also returns `holidays` of the current and next year `[ { key, name, date } ]` |
| `GET providers/me/blocks?from=&to=` | blocks overlapping the range, newest first |
| `POST providers/me/blocks/preview` | body = block; returns the active appointments (scheduled or confirmed) it overlaps: `[ { id, startsAt, endsAt, patientId, patientName } ]` |
| `POST providers/me/blocks` | body `{ kind, startsAt, endsAt, allDay, reason?, cancelAffected }`; 400 `invalid_range`, `invalid_kind`; when `cancelAffected`, the overlapped active appointments are cancelled in the same transaction through the existing cancellation path (reason "ausência do profissional", `CancelledBy` provider, existing cancellation notice); writes `block_created` |
| `DELETE providers/me/blocks/{id}` | writes `block_deleted`; never touches appointments |
| `GET providers/me/calendar-overlay?from=&to=` | `{ days: [ { date, windows: [ { startsAt, endsAt } ], holiday: { key, name } \| null } ], blocks: [ ... ] }`; range at most 62 days |

Create and reschedule (`POST`/`PUT providers/me/appointments`) gain `acknowledgeOutsideAvailability` (bool, default
false). When the check finds reasons and the flag is false: 409 `outside_availability` with
`{ reasons: [ { kind: "outside_hours" | "block" | "holiday", from?, to?, blockKind?, holidayName? } ] }`. With the flag:
saved, and an `appointment_outside_availability` audit entry (appointment id and reason kinds only).

All-day blocks are stored as local midnight to next local midnight in the provider's zone. Block reasons and patient
names never go to logs or audit values.

## 5. Provider UI

- **Ajustes → "Horários de atendimento"** (`DetailCard`, like the other settings sections):
  - a Monday-to-Sunday grid; each day "Não atendo" or one or more intervals (start, end), "+ intervalo", remove,
    "Copiar para os dias úteis";
  - "Vale a partir de" (date, default today); an upcoming version is shown with "Cancelar mudança";
  - "Feriados nacionais": "Atendo em feriados" switch (off by default) and the list of this and next year's holidays,
    each with date and a switch; saved with its own button.
- **Agenda:**
  - day and week views: hours outside the windows shaded; blocks and holidays as labelled bands ("Férias",
    "Bloqueio", "Feriado: Tiradentes");
  - new **month view** (selector Dia/Semana/Mês): a 6-week grid; each day shows up to 3 appointments by time and
    "+N", and a marker for holiday or block; clicking a day opens the day view;
  - "Bloquear período" button → dialog: kind (Férias, Bloqueio, Feriado local), from/to dates, all day or time range,
    reason (optional, max 200); after "Continuar" the preview lists affected appointments with "Manter as consultas" or
    "Cancelar e avisar os pacientes", or saves directly when none are affected;
  - "Próximos bloqueios" panel (next 90 days) with delete;
  - on create or reschedule, a 409 `outside_availability` shows the reasons in Portuguese and "Marcar mesmo assim".
- Copy in `pt.json` under `availability.*`, `blocks.*`, `calendar.month.*`.

## 6. Testing

- Holidays: dates for 2024–2030 against known values (Carnaval, Paixão, Corpus Christi included); keys and names.
- Versioning: a change from D closes the open version at D − 1, replaces later future versions, leaves the past
  untouched; D in the past is refused; cancelling an upcoming version reopens the previous one.
- Interval validation: overlap, start ≥ end, more than 6, non-5-minute steps.
- Overlay: windows per date across a version change inside the range; a disabled holiday; works-on-holidays; blocks
  overlapping the range edges; DST-safe conversion; range > 62 days refused.
- Outside check: outside hours, inside a block, on a holiday, inside the windows (no reasons); create/reschedule 409 and
  the acknowledged path with its audit entry; status change unaffected.
- Blocks: preview lists only active appointments overlapping; `cancelAffected` cancels them with the notice queued and
  the reason set; without it nothing changes; delete leaves appointments alone.
- Scoping (404 for another provider's block), 401, migration and migration-test chains.
- Front: provider build and lint.

## 7. Documentation

- Chapter `documentation/41-availability.md`; index.
- Status (F-29 delivery 2), gaps, Notion (F-29 SITUAÇÃO).
