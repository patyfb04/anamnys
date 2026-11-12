# Chapter 42: Recurring Appointments (F-29, delivery 3)

How the provider books a recurring appointment (weekly or every two weeks) as a series, reschedules one occurrence
without touching the others, moves "this and the following" sessions to a new day and time, and ends a series. Last
checked against the code on 2026-11-10 (branch `feature/agenda-series`, merged into develop 2026-11-11). Design:
`design/specs/2026-11-09-appointment-series-design.md`.

What a future change has to respect: an occurrence is a real `Appointments` row, so every other flow (statuses,
confirmations, reminders, attendance, availability, blocks) treats it like any appointment (section 2); generation
never cancels, never double books and never re-creates what was deleted or skipped (section 4); a split never loses an
occurrence that was individually changed, confirmed or already closed (section 6); series notices go out once, never
once per occurrence (section 8); and no patient name, last name or clinical content reaches logs, audit values or the
series e-mails (section 9).

---

## 1. Purpose and scope

F-29 (own agenda) is delivered in four parts:

1. Done: create, reschedule, statuses, day and week view, time zone per appointment.
2. Done: weekly availability, national holidays, blocks, overlay, outside-availability warning, month view (chapter 41).
3. **This delivery:** recurring appointments (`AppointmentSeries`), per-occurrence changes, split, end, notices.
4. Pending: the optional Google Calendar mirror (`CalendarConnections`).

Done when (backlog, this delivery's part): the provider books a recurring appointment and reschedules a single
occurrence, and nothing requires a Google account.

Out of scope: monthly or free-form recurrence; moving a series to another patient; patient self-booking (F-62).

---

## 2. Decisions

| Topic | Decision |
|---|---|
| Occurrences | Real `Appointments` rows, generated ahead for 12 weeks (84 days) and extended daily while the series is active. Nothing downstream changes. |
| Patterns | Weekly and every 2 weeks. Ends: never, on a date, or after N sessions (1 to 520). Weekday and time come from the first appointment, in `America/Sao_Paulo`. |
| One occurrence | The existing appointment routes. Rescheduling (time or modality) sets `Detached`; the series goes on. |
| "This and following" | The series is split: the old one ends before the chosen occurrence, a new one starts there. Untouched scheduled occurrences move; individually changed or confirmed ones stay and are listed before saving. |
| Slot taken | The occurrence is skipped (never double booked) but reported: in the response, and by a bell notice for the daily extension. |
| Notices | One summary e-mail on creation and on split, one notice on end. Each occurrence has its own confirmation request, scheduled ahead of it (section 8). The daily extension sends no e-mail. |
| Past start | Refused (400 `start_in_past`), unlike a single appointment: generation would otherwise create sessions that already happened. Generation also never creates an occurrence in the past (section 4). |
| Split or end from the first occurrence | The old series is cancelled (`CancelledAt`), because `EndsOn` cannot precede `StartsOn`. |

---

## 3. Data and migration

Migration `design/migrations/2026-11-10-appointment-series.sql` (idempotent, one transaction, mirrored in
`anamnys-db-script.sql` and appended to the chains of the other migration tests, after `2026-11-09-availability.sql`).
**It must be applied to any persistent environment before the new server version runs.** The `Appointment` entity now
maps `SeriesIndex` and `Detached`, so until the migration is applied **every appointment query fails** (`42703: column
"Detached" of relation "Appointments" does not exist`), not only the series routes. The dev database was in this state
when the delivery was built. It:

- adds to `AppointmentSeries`: `Interval` (int NOT NULL default 1, check 1..2), `Weekday` (int NOT NULL, check 0..6,
  0 = Sunday), `StartTime` (time NOT NULL), `Timezone` (default `America/Sao_Paulo`), `OccurrenceCount` (check 1..520),
  `GeneratedUntil` (date), `PreviousSeriesId` (self FK, ON DELETE SET NULL), `CreatedAt` (default now). Legacy rows, if
  any, get `Weekday` from `StartsOn` and `StartTime` 00:00;
- checks `AppointmentSeries_End_ck` (`EndsOn` and `OccurrenceCount` not both set) and `AppointmentSeries_Range_ck`
  (`EndsOn >= StartsOn`); the foreign key to `Providers` if missing (the one to `Patients` already existed); index on
  `PatientId`;
- adds to `Appointments`: `SeriesIndex` (int, 1-based, null for single appointments) and `Detached` (boolean NOT NULL
  default false); unique `Appointments_Series_Index_key` on (`SeriesId`, `SeriesIndex`) where `SeriesId` is not null;
  index `Appointments_Series_StartsAt_idx`;
- adds the audit actions `series_created`, `series_split` and `series_ended` to `AuditEntries_subject_ck` (subject
  `series`).

`RecurrenceRule` is written by the server as a readable description: `WEEKLY;INTERVAL={1|2};BYDAY={SU..SA}`. The new
notification kind `series_occurrence_skipped` needs no migration: `Notifications.Kind` has no check constraint. The two
reminder templates `series_scheduled` and `series_ended` are rows of `Reminders`, also without a constraint.

---

## 4. Rules: dates and generation

**Dates.** Occurrence k (1-based) falls on `StartsOn + (k - 1) x 7 x Interval` days; the instant is that local date at
`StartTime` in the series `Timezone`, so the local time is kept across DST. Duration is `DefaultMinutes`, modality
`DefaultModality`. The end is the last date `EndsOn`, or the date of session N; the series is `active` while that date
is today or later and it is not cancelled.

**Generation** (`SeriesGenerator.ExtendAsync`). Creates the occurrences with local date up to today + 84 days, stopping
at the end, as `scheduled`, `CreatedBy` provider, and sets `GeneratedUntil`. It:

- only visits dates after `GeneratedUntil`, so an occurrence that was deleted or skipped is not re-created later;
- skips an index that already exists (the unique index makes it idempotent);
- **skips an occurrence whose slot another active appointment holds** (no double booking), and returns it in
  `Skipped` (`seriesIndex`, `startsAt`). At creation, the first occurrence being taken is instead a 409 `overlap` with
  nothing written;
- **never creates an occurrence whose start is not after now**: a date already past is passed over silently (it is not
  a taken slot, so it is not in `Skipped` and raises no notice) and `GeneratedUntil` still moves past it. This matters
  when a series resumes after a long pause (a patient archived for months, a worker switched off): only the sessions
  from now on are created. Create refuses a past `startsAt` (400 `start_in_past`);
- creates occurrences that fall on blocks or holidays (the calendar shows them as outside availability); generation
  never cancels anything;
- caps a series at 520 occurrences, silently.

**Daily extension** (`SeriesExtensionJob`, a `BackgroundService` like `RetentionJob`, registered when
`Notifications:WorkerEnabled`): runs at start and then daily. One transaction per series, with the series row locked.
Skips cancelled series, archived or discarded patients and series generated to their end. It sends no e-mail. For each
skipped occurrence it writes one in-app notification: kind `series_occurrence_skipped`, subject `appointment_series`,
dedupe key `series_skipped:{seriesId}:{index}` (so never twice). The log has counts only. Discarding a patient
(chapter on retention) deletes these notices together with the patient's series, so the bell's unread count drops them.

---

## 5. Rules: single occurrence

Rescheduling an occurrence of a series (a change of time, duration or modality) sets `Detached = true`: it no longer
follows the pattern. A modality-only change also detaches it, even though it is not a reschedule for the availability
check (chapter 41, section 6). Cancelling one occurrence uses the existing route and leaves the series alone. A block
with "cancelar e avisar" cancels series occurrences like any appointment.

---

## 6. Rules: split and end

**Split** (`POST .../split`, from occurrence k, with a new weekday, start time, duration and modality). One transaction,
under the lock of the series. Only an occurrence that is scheduled or confirmed, not started, can be chosen, and the new
first instant cannot be in the past.

- The new first date is the chosen weekday on or after occurrence k's pattern date, at the new time.
- The old series ends the day before occurrence k (`EndsOn`, count cleared), or is cancelled when k is the first.
- The new series starts at the new first date, with `PreviousSeriesId` = old series; it takes the remaining count N - (k - 1)
  when the old one ended after N sessions, or the same `EndsOn`.
- Occurrences from k on that are **scheduled, not detached and not confirmed** move: they are deleted and regenerated by
  the new series, so their indexes restart at 1.
- Every other occurrence from k on stays and is **re-linked** to the new series with `Detached = true` under index
  `oldIndex - (k - 1)`: the detached and confirmed ones keep their time, and so do the cancelled, attended and no-show
  ones. The new series therefore never generates their week again (it would otherwise recreate a cancelled week).
- The first session the new series actually generates goes through the outside-availability check (409
  `outside_availability`, acknowledge to resend) and the slot pre-check. When occurrence k itself stays (confirmed or
  detached), its new-pattern date never exists, so that is the next index not held by a re-linked occurrence; when every
  index up to the end is held, nothing is checked. Slots taken are skipped and reported as `skipped`.
- The moving occurrences are deleted with their confirmation tokens: a confirmation request already e-mailed for one of
  them stops working (the link no longer finds the session). The patient gets only the new summary e-mail; the
  regenerated sessions get their own confirmation requests.
- Audit `series_split`, value with counts: moved, kept, relinked, skipped.

**Preview** (`POST .../split/preview`) writes nothing and answers `newFirstStartsAt`, `moving` (the count) and `staying`
(id, `startsAt`, reason `detached` or `confirmed`) for the ones from k that keep a session. `newFirstStartsAt` is the
first session the new series will actually generate (above): when occurrence k stays, the next one after it.

**End** (`POST .../end`, from date D, today or later). If D is on or before the first date the series is cancelled
(`CancelledAt`); otherwise `EndsOn` = D - 1 and the count is cleared. The scheduled and confirmed occurrences on or after
D are cancelled through the provider cancellation path (reason "série encerrada", `CancelledBy` provider) **without a
per-occurrence e-mail**, and one "series ended" notice is queued. Earlier occurrences are untouched. Audit `series_ended`.

---

## 7. Availability check

Only the first occurrence is checked, on create and on split: 409 `outside_availability` with the reasons of chapter 41,
section 8, and the same resend with `acknowledgeOutsideAvailability: true`. The occurrences that follow are not checked.

---

## 8. Notices

| When | What |
|---|---|
| Series created | One summary e-mail (`series_scheduled`): "Sessões marcadas com {provider}: {pattern} ({modality})", with what was actually created: "N sessões já estão marcadas." (and "As seguintes entram na agenda semana a semana." while generation has not reached the end); for a count-ended series with a taken slot "Foram marcadas M das N sessões."; plus the dates left out because the slot was taken. No per-occurrence "scheduled" e-mail. |
| Each occurrence | Its confirmation request at `StartsAt - max(48 h, AutoCancelHours + 24 h)`, or now if that is closer; 48 h when auto-cancel is off. The 1 h reminder is unchanged. |
| Split | One summary e-mail of the new pattern. |
| End | One "series ended" e-mail (`series_ended`) with the end date, attached to the first cancelled occurrence, or to the series' latest one if none was cancelled. |
| Daily extension | No e-mail. A taken slot becomes one bell notice (`series_occurrence_skipped`). |
| One occurrence rescheduled or cancelled | The existing per-appointment notices. |

**The confirmation lead.** A plain 48 h before the session would, for a provider whose auto-cancel window is 48 h or
more, put the deadline before the request and so silently switch auto-cancel off for series occurrences. With
`max(48 h, AutoCancelHours + 24 h)` the deadline always falls after the request and before the session, in both modes
(`after_email` and `before_session`). The cost: a patient of a provider with a long auto-cancel window is asked earlier
than 48 h. Example: `AutoCancelHours` 72 gives the request at T - 96 h and the deadline at T - 24 h (`after_email`) or
T - 72 h (`before_session`) (`ConfirmationRules.SeriesConfirmationLeadFor`, read once per generation).

The booking policy is read **at generation time**: the send time of each confirmation request is fixed when its
occurrence is created (up to 84 days ahead). Changing `AutoCancelHours` later does not move requests already scheduled;
only occurrences generated afterwards use the new lead. (The deadline itself is computed when the request is sent.)

The series notices are `Reminders` rows on an occurrence. The worker sends them through the series, looked up by the
occurrence's `SeriesId`, and the occurrence's own status does not make them stale: one occurrence being rescheduled or
cancelled does not withdraw them (`CancelPendingAsync` leaves them alone).

---

## 9. Audit and privacy

| Action | Subject / value |
|---|---|
| `series_created` | `series`: ids, dates, counts |
| `series_split` | `series`: ids, dates, `moved m kept s relinked r skipped x` |
| `series_ended` | `series`: ids, date, cancelled count |

Values carry ids, dates and counts only. The e-mails carry the provider, the pattern and dates, never the patient's last
name or clinical content. The extension logs counts, not series or patient ids.

---

## 10. Endpoints and codes

All under `/api/phi/providers/me`, cookie session, 401 first, foreign ids 404, `Cache-Control: no-store`. Dates
`yyyy-MM-dd`, times `HH:mm`, instants ISO with offset, weekday 0 = Sunday to 6 = Saturday. Field validation problems
are the standard `ValidationProblem` (400, `errors`, no `code`); the codes below come as `{ code, message }`.

| Method and path | Success | Errors |
|---|---|---|
| `POST appointment-series` | 201 `{ seriesId, firstAppointmentId, generated, skipped[] }`; body `{ patientId, startsAt, durationMinutes, modality, interval, endsOn?, occurrenceCount?, acknowledgeOutsideAvailability? }` | 400 validation; 400 `invalid_interval`, `invalid_end` (both ends, end before the first date, count outside 1..520), `start_in_past` (`startsAt` not after now); 404 patient; 422 archived patient; 409 `outside_availability`, `overlap` |
| `GET appointment-series/{id}` | 200 `{ id, patientId, interval, weekday, startTime, timezone, startsOn, endsOn, occurrenceCount, active, description, upcoming[] }`; `upcoming` = `{ id, startsAt, status, seriesIndex, detached }` of the occurrences not yet over | 404 |
| `GET patients/{patientId}/appointment-series` | 200 bare array of the patient's active series (no `upcoming`) | 404 patient |
| `POST appointment-series/{id}/split/preview` | 200 `{ newFirstStartsAt, moving, staying[] }`; body `{ fromAppointmentId, weekday, startTime, durationMinutes, modality }` | 400 validation, `invalid_end`; 404 (also an occurrence of another series); 409 `invalid_occurrence`, `series_ended` |
| `POST appointment-series/{id}/split` | 200 `{ newSeriesId, skipped[] }`; same body + `acknowledgeOutsideAvailability?` | as the preview, plus 409 `outside_availability`, `overlap` |
| `POST appointment-series/{id}/end` | 200 `{ cancelled }`; body `{ fromDate }` | 400 `invalid_date` (missing or past); 404; 409 `series_ended` (already cancelled or ending before the date) |
| `GET appointments` | items gain `seriesId`, `seriesIndex`, `detached`, `seriesDescription` (e.g. "toda terça às 14:00", "terça sim, terça não, às 14:00") | as before |
| `GET notifications` | new kind `series_occurrence_skipped` (`appointmentId` null; `patientName`, `patientId`, `seriesId`, `startsAt` = the start it would have had) | |

There is no appointment detail route on the server: the UI reads series fields from the list item.

---

## 11. Provider UI

- **`AppointmentFormModal`** (create): "Repetir" (Não repete / Toda semana / A cada 2 semanas); when repeating, "Termina"
  (Nunca / Em [data] / Após [N] sessões) and a summary line. Client checks: end date required, count 1 to 520. Posts to
  `appointment-series`; reuses the outside-availability reasons and "Marcar mesmo assim". After creation a toast "Série
  criada: N sessões marcadas."; if `skipped` is not empty, a panel "Não foi possível marcar: 24/11 (horário ocupado)."
  Edit mode is unchanged.
- **Cards and month view:** a small repeat icon on every occurrence with a `seriesId` (detached ones too).
- **`AppointmentDetailsModal`:** "Faz parte de uma série: {description} (sessão 4 de 10)" (total from `GET
  appointment-series/{id}`; "sessão 4" with no end); a detached occurrence adds that it no longer follows the series.
  For a scheduled or confirmed occurrence not yet started, "Remarcar" asks "Só esta sessão" or "Esta e as seguintes"
  (opens `SeriesSplitModal`), and "Cancelar consulta" asks "Só esta sessão" or "Encerrar a série a partir desta"
  (inline confirmation, then `end` from the occurrence's local date). The choices are hidden for started, closed or
  inactive-series occurrences.
- **`SeriesSplitModal`:** weekday, time, duration and modality (defaults from the occurrence); "Ver o que muda" calls the
  preview and lists the new first session, the moving count and the staying ones ("confirmada pelo paciente" or
  "alterada uma a uma"); then "Remarcar as seguintes". Changing a field clears the preview. 409 `outside_availability`
  shows the reasons and "Marcar mesmo assim"; skipped dates appear in a panel.
- **`PatientSeriesCard`** (patient page, above attendance): hidden with no active series; each series with its
  description, "desde", the end, and "Encerrar série", which ends from the next active session's date (today if none)
  after a confirmation. The confirmation text says the patient is told only "Se o paciente tiver e-mail cadastrado"
  (same in the details modal). For an archived patient the card is read-only: no "Encerrar série", and a note that new
  sessions do not enter the calendar until the patient is reactivated (the daily extension skips archived patients).
- **Bell:** `series_occurrence_skipped` reads "Sessão de {patient} em {dd/MM HH:mm} não foi marcada: o horário já estava
  ocupado" and links to the day view of that date.
- Shared: `packages/shared/src/api/appointmentSeries.ts`, types in `types.ts`, copy in `pt.json` under `series.*` and
  `notifications.seriesSkipped`; `components/calendar/series.ts` (pattern text, skipped dates, error messages for the
  codes). Hooks in `hooks/useAppointments.ts`; the series mutations invalidate appointments, series, attendance,
  timeline and patient.

---

## 12. Tests

Under `anamnys-aspire.Tests/Series/` (`SeriesGeneratorTests`, `SeriesServiceTests`, `SeriesEndpointTests`) and
`Data/AppointmentSeriesMigrationTests.cs`, on scratch databases from the bootstrap (`SeriesScene`), because the dev
database may lack the migration:

- Generation: weekly and every two weeks; end by date, by count, never; the 84-day horizon; idempotent extension; DST
  keeps the local time; occurrences on blocks or holidays are created; a taken slot is skipped and reported; the
  extension notice, its dedupe and its inbox item; cancelled series and archived patients skipped; a series resumed
  100 days after its coverage creates only future occurrences and reports nothing; a past start is refused.
- Single: reschedule and modality change set `Detached`; cancelling one leaves the others.
- Split: linked series; only untouched scheduled occurrences move; detached, confirmed, cancelled, attended and no-show
  ones are re-linked and a cancelled week is not recreated; remaining count; availability on the new first date;
  preview; from the first occurrence; when occurrence k stays, the preview and the availability check use the first
  session actually generated.
- Discard (`DiscardServiceTests`): a series' skipped-slot notice is deleted with the patient's series and leaves the
  unread count.
- End: future active occurrences from D cancelled, earlier untouched, one notice, no per-occurrence e-mail.
- Notices: one summary on creation (text with created and skipped), confirmation lead including the `AutoCancelHours` 72
  case in both modes, none on extension.
- A block with "cancelar e avisar" cancels series occurrences; scoping and 401 over HTTP; the migration and every
  `*MigrationTests` chain.

Runs: Task 1 fix round 1, `-class "*Series*" -class "*MigrationTests*"` 199/199 passed. Provider and patient build and
lint clean. Not exercised against a running server or in a browser. The existing dev-database tests that create
appointments (`AppointmentsTests`, `AppointmentNotificationsTests`, `AppointmentConfirmationsTests`,
`NotificationWorkerStepsTests`, and the endpoint tests) failed 65 times while the migration was not applied to the dev
database (`42703` or an HTTP 500 for the same reason); it was applied on 2026-11-10 and they pass; re-run
them then, with Aspire stopped.

---

## 13. Limits and known gaps

- Monthly and free-form recurrence do not exist; a series is weekly or every two weeks.
- A series cannot be moved to another patient.
- The 520-occurrence cap is silent; some validation messages duplicate those of the single-appointment form.
- After a split, the summary's "N sessões já estão marcadas" also counts re-linked cancelled (or closed) sessions.
- A series ended (or cancelled by a split from its first occurrence) before the worker sent its summary yields both
  e-mails: "Sessões marcadas" and then "Sessões encerradas".
- A single reschedule does not take the series lock; racing a split on the same occurrence can surface as a 500.
- A skipped occurrence is never retried: the provider books that week by hand.
- Only the first occurrence goes through the outside-availability check.
- The Google mirror is delivery 4.
- Not checked in a browser or against a live server; the Confluence documentation is pending.

---

## 14. Where things are

| Piece | Path |
|---|---|
| Migration | `design/migrations/2026-11-10-appointment-series.sql` |
| Entities | `anamnys-aspire.Server/Data/Entities/AppointmentSeries.cs`, `Appointment.cs` |
| Server code | `anamnys-aspire.Server/Appointments/Series/` (`SeriesContracts`, `SeriesGenerator`, `SeriesService`, `SeriesEndpoints`, `SeriesExtensionJob`) |
| Hooks | `Appointments/Appointments.cs` (detach on reschedule, list fields, `CancelByProviderAsync`), `Notifications/AppointmentNotifications.cs`, `AppointmentEmails.cs`, `ConfirmationRules.cs`, `NotificationWorkerSteps.cs`, `ProviderNotifications.cs` |
| Provider UI | `apps/provider/src/components/calendar/` (`SeriesSplitModal`, `PatientSeriesCard`, `series.ts`, `AppointmentFormModal`, `AppointmentDetailsModal`) |
| API client | `packages/shared/src/api/appointmentSeries.ts` |
| Tests | `anamnys-aspire.Tests/Series/`, `anamnys-aspire.Tests/Data/AppointmentSeriesMigrationTests.cs` |
