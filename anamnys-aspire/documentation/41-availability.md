# Chapter 41: Availability, Blocks and Month View (F-29, delivery 2)

How the provider publishes the weekly hours they work, blocks absences (vacation, blocks, local holidays), sees the
national holidays, and is warned (never stopped) when an appointment falls outside all that. Plus the month view of the
agenda. Last checked against the code on 2026-11-09 (branch `feature/agenda-f29`, merged into develop 2026-11-09). Design:
`design/specs/2026-11-09-availability-design.md`.

What a future change has to respect: availability is a guide for the provider, never a hard rule, until the patient
can book by themselves (F-62); rules belong to a version header and a saved version is never edited, only closed or
replaced (section 4); nothing is cancelled that the provider did not see in the preview (section 7); status changes
never run the outside check (section 6); and block reasons and patient names never reach logs or audit values
(section 5).

---

## 1. Purpose and scope

F-29 (own agenda) is delivered in four parts:

1. Done earlier: create, reschedule, statuses, day and week view, time zone per appointment.
2. **This delivery:** weekly availability with effective dates, national holidays, blocks, the overlay on the calendar,
   the outside-availability warning on create and reschedule, and the month view.
3. Pending: recurring appointments (`AppointmentSeries`).
4. Pending: the optional Google Calendar mirror (`CalendarConnections`).

Done when (backlog, this delivery's part): the provider defines weekly availability and blocks a week of vacation, and
nothing requires a Google account.

Out of scope: booking by the patient with free slots, holds and booking policies (F-62, patient portal); `SlotMinutes`
of the rules (stored, unused); local holidays from an external service (the provider records them as blocks of kind
`holiday`).

---

## 2. Decisions

| Topic | Decision |
|---|---|
| Booking outside availability | Warn and allow. The server answers 409 `outside_availability` with the reasons; the provider confirms with "Marcar mesmo assim". |
| Provider with no saved hours | No `outside_hours` reason (blocks and holidays still apply). Otherwise every existing provider would be warned on every booking. A version that starts in the future does not make earlier bookings warn. |
| Holidays | Computed by the server. Default: the provider does not work on holidays; they can switch the whole thing on or individual holidays off. |
| Block over appointments | The preview lists them; the provider chooses to keep or cancel them. Cancelling only touches the ids the provider saw (section 7). |
| Modality-only edit | Not a reschedule, so no check. |
| All-day blocks | Inclusive of both local dates. |
| All-closed version | Still a version: visible, cancellable, and from its date every booking warns `outside_hours`. |

---

## 3. Data and migration

Migration `design/migrations/2026-11-09-availability.sql` (idempotent, one transaction, mirrored in
`anamnys-db-script.sql` and appended to the chains of the other migration tests). **It must be applied to any
persistent environment before the new server version creates or reschedules an appointment.** Until then both fail,
because the outside check reads `ProviderHolidaySettings`, which does not exist. It:

- creates `ProviderHolidaySettings`: `ProviderId` (PK, FK `Providers` ON DELETE CASCADE), `WorksOnHolidays` (default
  false), `DisabledHolidays text[]` (default `{}`);
- adds the foreign keys `AvailabilityRules` and `AvailabilityExceptions` to `Providers` if missing (CASCADE), the index
  `AvailabilityRules_Provider_EffectiveFrom_idx`, and the check `AvailabilityExceptions_Reason_ck` (at most 200
  characters; longer legacy reasons are cut first). The index (`ProviderId`, `StartsAt`) of the spec already exists as
  `AvailabilityExceptions_Provider_Range_idx`, so none was added;
- creates `AvailabilityVersions`: `Id`, `ProviderId` (FK CASCADE), `EffectiveFrom`, `EffectiveTo`, `CreatedAt`; unique
  on (`ProviderId`, `EffectiveFrom`), range check. It is the header of a version. `AvailabilityRules` gains `VersionId`
  (NOT NULL, FK CASCADE, indexed); legacy rules, if any, get one header per provider and start date. The header exists
  so that a version where every day is closed (no rules) can be seen and cancelled;
- adds the audit actions `availability_changed`, `block_created`, `block_deleted` and
  `appointment_outside_availability` to `AuditEntries_subject_ck`.

Existing tables used: `AvailabilityRules` (one row per interval per weekday per version: `Weekday` 0 to 6 with 0 =
Sunday, `StartsAtTime`, `EndsAtTime`, `EffectiveFrom`, `EffectiveTo`, `Timezone`; the rule copies the dates of its
header so the overlay reads rules alone) and `AvailabilityExceptions` (the blocks: `StartsAt`, `EndsAt`, `AllDay`,
`Kind` `block|vacation|holiday`, `Reason`).

---

## 4. Rules: versions, intervals, time zone

**Versioning.** `PUT availability` with `effectiveFrom` D, inside one transaction that locks the provider row
(`SELECT ... FOR UPDATE`):

- deletes the headers that start on or after D (their rules go with them: a future version replaced by a newer
  decision);
- closes the header open on D, and its rules, at D - 1;
- writes a new header and its rules from D with `EffectiveTo` null.

Nothing before D changes. D cannot be before today in the provider's local date (`effective_in_past`).
`DELETE availability/upcoming/{D}` deletes the header and rules and gives the previous version the cancelled version's
end date again; a version that starts today or earlier cannot be cancelled (404).

**Intervals.** Per weekday of one version: start < end, no overlap, at most 6 intervals, times on 5-minute steps,
latest end `23:55` (`24:00` is not accepted). A weekday with no intervals means "Não atendo". `days` is required; `[]`
means closed all week.

**Time zone.** Rules are converted to instants in the `Timezone` of their own row; local dates, all-day blocks,
holidays and "today" use the provider zone, which is `Appointments.PracticeTimezone` (`America/Sao_Paulo`)
(`AvailabilityTime`). A time inside a DST gap moves to just after the gap; a time that happens twice takes its first
occurrence.

**Serialisation.** Availability, holiday-settings and block writes lock the provider row, so two of them never
interleave.

---

## 5. Audit

Values hold no names and no block reasons:

| Action | Subject / value |
|---|---|
| `availability_changed` | `availability`: `from D` or `cancel D`; `holidays`: `works yes|no; off k1,k2` for holidays |
| `block_created` | `block`: `<id> <kind> <start> <end> cancelled N` |
| `block_deleted` | `block`: `<id> <kind>` |
| `appointment_outside_availability` | `appointment`: `<id> kind1,kind2` |

---

## 6. National holidays, overlay, outside check

**Holidays** (`NationalHolidays.For(year)`, computed once per year, in date order). Keys are stable (they are stored in
`DisabledHolidays`):

| Key | Name | Date |
|---|---|---|
| `confraternizacao_universal` | Confraternização Universal | 01/01 |
| `carnaval_segunda` | Carnaval (segunda-feira) | Easter - 48 |
| `carnaval_terca` | Carnaval (terça-feira) | Easter - 47 |
| `sexta_feira_da_paixao` | Sexta-feira da Paixão | Easter - 2 |
| `tiradentes` | Tiradentes | 21/04 |
| `dia_do_trabalho` | Dia do Trabalho | 01/05 |
| `corpus_christi` | Corpus Christi | Easter + 60 |
| `independencia` | Independência | 07/09 |
| `nossa_senhora_aparecida` | Nossa Senhora Aparecida | 12/10 |
| `finados` | Finados | 02/11 |
| `proclamacao_da_republica` | Proclamação da República | 15/11 |
| `consciencia_negra` | Dia Nacional de Zumbi e da Consciência Negra | 20/11 |
| `natal` | Natal | 25/12 |

Easter by the anonymous Gregorian algorithm (Meeus/Jones/Butcher). A holiday **applies** unless the provider works on
holidays or its key is in `DisabledHolidays`; with no `ProviderHolidaySettings` row, all apply.

**Overlay** (`AvailabilityOverlay`, range at most 62 days): for each local date, the windows of the version in force on
that date (as instants), and the applicable holiday or null; plus the blocks that overlap the range. Windows are not
cut by blocks or holidays: they are returned separately so the UI draws each.

**Outside check** (`AvailabilityCheck.ReasonsAsync`, on create and reschedule only). Reasons, in this order:

- `outside_hours`: only when the provider has a version in force on or before the appointment's last date, and the
  windows of the days it touches do not cover it without a gap;
- `block`: one per overlapping block (`from`, `to`, `blockKind`);
- `holiday`: one per applicable holiday on a day the appointment touches (`from`/`to` = that local day, `holidayName`).

Status changes never run the check, nor does a PUT that changes only the modality. The 409 is decided before the
database overlap constraint, so an overlapping slot outside hours answers `outside_availability` first and the
overlap error after it is acknowledged.

---

## 7. Blocks

Kinds `block`, `vacation`, `holiday` (local holiday). Reason at most 200 characters; range at most 367 days.

- **All-day:** runs from the local date of `startsAt` to the local date of `endsAt`, both included, stored as local
  midnight to the next local midnight. The UI sends "10 to 20" as 10/01 00:00 and 20/01 00:00.
- **Preview** lists the scheduled or confirmed appointments that overlap and are not over yet (`EndsAt` after now).
- **Create with `cancelAffected`:** `expectedAppointmentIds` (the ids the preview returned; `[]` when it was empty) is
  required. Under the provider lock the server recomputes the affected set. Equal to the expected set (order ignored):
  the block and the cancellations are saved in one transaction, each through `Appointments.CancelByProviderAsync`
  (status, `CancelledAt`, `CancelledBy` provider, reason "ausência do profissional", and the existing cancellation
  notice). Different: 409 `changed` with the current list, nothing written. A database-constraint race answers the
  same. This is what guarantees nothing is cancelled that the provider did not see.
- Without `cancelAffected`, no appointment is touched. Deleting a block never touches appointments.

---

## 8. Endpoints and codes

All under `/api/phi/providers/me`, cookie session, 401 first, `Cache-Control: no-store`. Times of day `HH:mm`, dates
`yyyy-MM-dd`, instants ISO with offset. 400 bodies are `{ message, code }`. Absent fields are omitted, not null.

| Method and path | Success | Errors |
|---|---|---|
| `GET availability` | 200 `{ current, upcoming[] }`; a version is `{ effectiveFrom, days:[{weekday, intervals:[{start,end}]}] }` with all 7 weekdays | 401 |
| `PUT availability` | 200 (GET shape); body `{ effectiveFrom, days }` | 400 `invalid_intervals` (also a missing `days`), `effective_in_past`, `invalid_effective_from` |
| `DELETE availability/upcoming/{date}` | 204 | 404 (unknown, starts today or earlier, malformed date) |
| `GET holiday-settings` | 200 `{ worksOnHolidays, disabledHolidays, holidays:[{key,name,date}] }` (this and next year, 26 items) | 401 |
| `PUT holiday-settings` | 200 (GET shape); body `{ worksOnHolidays, disabledHolidays }` | 400 `invalid_holidays` (unknown key, missing `worksOnHolidays`) |
| `GET blocks?from=&to=` | 200 `[{id, kind, startsAt, endsAt, allDay, reason}]`, `StartsAt` descending | 400 `invalid_range` (`to` after `from`, at most 367 days) |
| `POST blocks/preview` | 200 `[{id, startsAt, endsAt, patientId, patientName}]` | 400 `invalid_kind`, `invalid_range`, `invalid_reason` |
| `POST blocks` | 201 `{ id, cancelledAppointmentIds }`; body `{ kind, startsAt, endsAt, allDay, reason?, cancelAffected, expectedAppointmentIds? }` | 400 `invalid_kind`, `invalid_range`, `invalid_reason`, `expected_appointments_required`; 409 `changed` `{ message, appointments }` |
| `DELETE blocks/{id}` | 204 | 404 (unknown or another provider's) |
| `GET calendar-overlay?from=&to=` | 200 `{ days:[{date, windows:[{startsAt,endsAt}], holiday:{key,name}\|null}], blocks[] }`, both dates included, at most 62 days | 400 `invalid_range` |
| `POST`/`PUT appointments` | as before, plus `acknowledgeOutsideAvailability` (default false) | 409 `outside_availability` `{ message, reasons:[{kind, from?, to?, blockKind?, holidayName?}] }` |

Reasons: `outside_hours` has only `kind`; `block` has `from`, `to`, `blockKind`; `holiday` has `from`, `to` and
`holidayName`. With the flag set, the appointment is saved and `appointment_outside_availability` is audited.

Pass `from`/`to` of `GET blocks` with `Z` or URL-encoded: an unencoded `+01:00` becomes a space and fails to bind.

---

## 9. Provider UI

- **Ajustes, "Horários de atendimento"** (`AvailabilitySection`, first card): Monday-to-Sunday grid with a switch per
  day ("Não atendo" when off), time inputs on 5-minute steps (max 23:55), "+ Intervalo" (max 6), remove, "Copiar para
  os dias úteis" (to Monday to Friday), client validation mirroring the server, "Vale a partir de" (default today),
  a note when the save replaces an upcoming version, upcoming versions with a weekday summary and "Cancelar mudança".
  With no saved hours it suggests Monday to Friday 08-12 and 13-18 and says nothing applies until saved.
  `HolidaySettingsPanel` (same card, own save button): "Atendo em feriados" and this and next year's holidays, each with
  a switch (keyed, so it applies to both years; disabled while "Atendo em feriados" is on).
- **Agenda:** `CalendarGrid` shades the hours outside the windows and draws holiday and block bands ("Feriado: X",
  "Férias", "Bloqueio: motivo"); shading only happens on dates where hours are known (section 11). The header offers
  Dia/Semana/Mês and "Bloquear período". `MonthView`: 6-week grid starting Monday, up to 3 appointments by time and
  "+N consultas", holiday and block markers, days outside the month dimmed, a click opens the day view. Appointments
  load for 42 days (the list limit), the overlay for 42 days. Mobile keeps the day view only.
- **`BlockPeriodModal`:** kind, from and to dates, "Dia inteiro" or times, reason with counter. "Continuar" calls the
  preview; an empty list saves directly; otherwise the list with "Manter as consultas" or "Cancelar e avisar os
  pacientes" (sends `expectedAppointmentIds`). A 409 `changed` replaces the list, warns and asks again.
- **`UpcomingBlocksPanel`:** "Próximos bloqueios" (next 90 days) with delete and an inline confirmation ("As consultas
  não mudam").
- **`AppointmentFormModal`:** a 409 `outside_availability` shows the reasons in Portuguese (`outsideReasons.ts`) and
  swaps the primary button to "Marcar mesmo assim", which resends with the flag; any field change clears the reasons.
- Shared: `packages/shared/src/api/availability.ts` (`availabilityApi`, `blocksApi`), types in `types.ts`, and
  `ApiError.body` (the raw response body, needed for the 409 lists). Copy in `pt.json` under `availability.*`,
  `blocks.*`, `calendar.month.*`, `calendar.overlay.*`. Hooks in `hooks/useAvailability.ts`.

---

## 10. Tests

Under `anamnys-aspire.Tests/Availability/` and `Data/AvailabilityMigrationTests.cs`, on scratch databases built from the
bootstrap (`AvailabilityScene`), because the dev database may lack the migration:

- `NationalHolidaysTests`: 2024 to 2030 against known dates, keys, names, applicability.
- `AvailabilityValidationTests`: overlap, start >= end, more than 6, non-5-minute steps, 23:55.
- `AvailabilityRulesTests`: a change from D closes the open version at D - 1 and replaces later ones, the past is
  untouched, D in the past refused, cancelling reopens the previous version, an all-closed version is visible and
  cancellable, holiday settings.
- `AvailabilityOverlayTests`: windows across a version change, disabled holiday, works-on-holidays, blocks at the range
  edges, DST, range over 62 days refused.
- `AvailabilityAppointmentsTests`: outside hours, block, holiday, inside (no reasons), the 409 and the acknowledged path
  with its audit entry, status change and modality-only change unaffected, no hours saved, future version.
- `BlocksTests`: preview lists only active, not-over appointments; `cancelAffected` with the notice queued; an
  appointment booked between preview and save gives `changed` and nothing is cancelled; `expected_appointments_required`;
  delete leaves appointments alone.
- `AvailabilityEndpointTests`: 401 over HTTP (10 routes against the live app), handlers on scratch databases for shapes
  and 404.
- `AvailabilityMigrationTests`, plus every `*MigrationTests` chain now ends with the new file.

Runs: Task 1 fix round 1, `*Availability*`, `BlocksTests` and `NationalHolidaysTests` 65/65; all `*MigrationTests`
164/164. Provider and patient build and lint clean. Not checked in a browser. The existing dev-database tests that
create appointments (`AppointmentsTests`, `AppointmentNotificationsTests`, `AppointmentConfirmationsTests`,
`NotificationWorkerStepsTests`) need the migration applied there; they were not run in this delivery.

---

## 11. Limits and known gaps

- Past dates under an older version that had a weekday closed may not be shaded: the overlay carries no "version in
  force" flag, and empty windows mean either "closed" or "no hours saved". Cosmetic.
- There is no concurrency test for the block lock (two simultaneous block writes, or a block against a booking).
- Recurrence and the Google mirror are pending (deliveries 3 and 4).
- Patient booking with free slots is F-62; until then availability never blocks anything.
- Local holidays are manual blocks; there is no municipal calendar.
- Clicking a shaded slot, a block or a holiday still opens "Nova consulta"; the server warning decides.
- `SlotMinutes` is unused.

---

## 12. Where things are

| Piece | Path |
|---|---|
| Migration | `design/migrations/2026-11-09-availability.sql` |
| Entities | `anamnys-aspire.Server/Data/Entities/AvailabilityRule.cs`, `AvailabilityRuleVersion.cs`, `AvailabilityException.cs`, `ProviderHolidaySettings.cs` |
| Server code | `anamnys-aspire.Server/Availability/` |
| Appointment hooks | `Appointments/Appointments.cs` (`CreateWithReasonsAsync`, `UpdateWithReasonsAsync`, `CancelByProviderAsync`), `AppointmentEndpoints.cs` |
| Provider UI | `apps/provider/src/components/calendar/`, `components/settings/AvailabilitySection.tsx`, `HolidaySettingsPanel.tsx` |
| API client | `packages/shared/src/api/availability.ts` |
| Tests | `anamnys-aspire.Tests/Availability/`, `anamnys-aspire.Tests/Data/AvailabilityMigrationTests.cs` |
