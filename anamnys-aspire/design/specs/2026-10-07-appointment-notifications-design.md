# Appointment Notifications

**Date:** 2026-10-07
**Status:** Approved
**Scope:** E-mail confirmation of every new or rescheduled appointment through a one-time
link, configurable automatic cancellation of unconfirmed appointments, a reminder 1 hour
before the session, a cancellation e-mail, and an in-app bell that tells the provider when a
patient confirms or the system cancels. Builds on `2026-10-05-provider-calendar-design.md`.
E-mail is the only channel in this delivery; WhatsApp and the rest of what was left out are
listed in `design/gaps/2026-10-03-patient-flows-gaps.md` ("Notificações").

The plan is ordered in two phases so phase A is usable on its own:

- **Phase A:** schema, outbox and worker, confirmation and cancellation e-mails, the 1-hour
  reminder, automatic cancellation, the public confirmation page and the portal's "Confirmar"
  button.
- **Phase B:** the provider's notification bell and the "Confirmação de consultas" settings.
  Until phase B ships, every provider gets the default rule (cancel 1 hour after the e-mail).

---

## 1. Context

- Appointments are created, rescheduled, cancelled and change status only through
  `Appointments/Appointments.cs` (provider calendar). Patient self-booking does not exist yet
  (calendar delivery 4); everything here is triggered by those methods, so it will apply to
  self-booking too.
- `Appointments.Status` already has `scheduled` and `confirmed`; `CancelledBy` already accepts
  `system`.
- `Patients.ContactEmail` is where scheduling notices go (see the comment in `Patient.cs`); it
  is optional.
- `IEmailSender` (Resend in production, Mailpit in dev) sends mail; it is optional by design
  (no `Email:Provider`, no sender). Production e-mail is blocked by the unverified Resend
  domain (gaps file).
- Portal invitations already use a one-time token stored only as a SHA-256 hash
  (`PatientInvitationTokens`) and a link built from `PatientPortal:BaseUrl`
  (`/patient/convite/{token}`).
- Existing tables used here: `Reminders` (per appointment: `Channel`, `TemplateKey`,
  `ScheduledFor`, `SentAt`, `DeliveryStatus`), `Notifications` (per provider: `Kind`,
  `SubjectType`, `SubjectId`, `Channel` incl. `in_app`, `ScheduledFor`, `SentAt`,
  `DeliveryStatus`, `ReadAt`) and `BookingPolicies` (one optional row per provider). None of
  them has an EF entity or a writer yet.
- The provider app's top bar already shows a bell icon that does nothing.
- No background worker exists; the only hosted service is `DatabaseInitializer`.

## 2. Decisions

| Topic | Decision |
|---|---|
| Who gets the confirmation e-mail | The patient, for every new appointment and every reschedule, whoever created it (provider now, patient self-booking later). |
| Confirming | Link with a one-time token, no login. The link opens a page showing the appointment and a **"Confirmar presença"** button; opening the link alone confirms nothing, because mail scanners open links. A patient with a portal account can also confirm from the portal. The provider's existing "Confirmar" button has the same effect. |
| Automatic cancellation | Configurable per provider: off; N hours after the confirmation e-mail; or N hours before the session. Default: 1 hour after the e-mail. |
| Reminder | One e-mail 1 hour before every appointment still `scheduled` or `confirmed`. If still unconfirmed, it carries the confirmation link. |
| Cancellation e-mail | Sent to the patient whenever an appointment is cancelled, by the provider or the system. |
| Provider notice | In-app bell only: "patient confirmed" and "cancelled automatically". No e-mail to the provider. |
| Channel | E-mail only. Reminders are stored per channel so WhatsApp can be added later. |
| E-mail content | The minimum: provider's name, date, time, modality, and the link when there is one. Nothing clinical. |
| Sending | Never inside the request. Every patient e-mail is a `Reminders` row written in the same transaction as the appointment change, and a background worker sends it. A mail failure never fails the appointment change. |
| Scheduling mechanism | A `BackgroundService` polling the database every minute. No Hangfire, Quartz or Redis queue. |

### Edge cases

| Case | Behaviour |
|---|---|
| Appointment starts in less than 1 hour | Confirmation e-mail only; no reminder. |
| Deadline falls after the session starts | The deadline is capped at the session's start. |
| Deadline is already past when the e-mail is sent (e.g. "24 h before" for a session 3 h away), or the rule is off | No automatic cancellation for that appointment; it stays `scheduled` and the provider decides. |
| Patient has no `ContactEmail` | No e-mails and no automatic cancellation. The calendar shows "Paciente sem e-mail: não receberá confirmação." |
| No e-mail sender configured, or the confirmation e-mail fails for good | Rows stay `pending` (or become `failed`); the worker logs once per run that sending is disabled. The deadline only starts when the confirmation e-mail is actually sent, so nothing is cancelled for a patient who never got the e-mail. |
| Reschedule | The previous link stops working; the appointment returns to `scheduled`; a new confirmation e-mail, deadline and reminder replace the old ones. |
| Provider presses "Confirmar" | Same as a patient confirmation, but no bell notice (the provider did it). |
| Cancellation (any source) | Pending reminders of that appointment become `cancelled`; a cancellation e-mail is queued (if the patient has an e-mail). |
| Status leaves `scheduled`/`confirmed` another way (`attended`, `no_show`) | Pending reminders become `cancelled`. |
| Undo back to `scheduled` (from `attended`/`no_show`) | No new e-mails: the session has already started. |
| Link opened after the session started, after a reschedule or after a cancellation | The page says the link is no longer valid and does not confirm. |

## 3. Data model

One migration, `design/migrations/2026-10-07-appointment-notifications.sql`, idempotent and
all-or-nothing like the earlier ones; the same changes go into `anamnys-db-script.sql`.

### New table `AppointmentConfirmations`

One row per confirmation e-mail.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `AppointmentId` | uuid NOT NULL | FK `Appointments` ON DELETE CASCADE |
| `CreatedAt` | timestamptz NOT NULL | when the e-mail was queued |
| `ExpiresAt` | timestamptz NOT NULL | the appointment's start |
| `DeadlineAt` | timestamptz NULL | automatic cancellation moment, set when the confirmation e-mail is sent; NULL = none (yet) |
| `ConfirmedAt` | timestamptz NULL | |
| `ConfirmedBy` | text NULL | `patient` or `provider` |
| `ClosedAt` | timestamptz NULL | set when superseded by a reschedule or the appointment is cancelled |

Checks: `ConfirmedBy` in (`patient`, `provider`) and present exactly when `ConfirmedAt` is;
not both `ConfirmedAt` and `ClosedAt`. Index on `DeadlineAt` where
`ConfirmedAt IS NULL AND ClosedAt IS NULL AND DeadlineAt IS NOT NULL`. There is deliberately no unique
index for "at most one open row per appointment": closing the old row and inserting the new one
happen in one `SaveChanges`, whose statement order EF does not guarantee, so such an index would
fail intermittently. The rule is enforced in code by `AppointmentNotifications` and covered by tests.

### New table `AppointmentConfirmationTokens`

One row per link sent. The confirmation e-mail and the reminder each carry their own token,
generated by the worker at send time, so no usable token is ever stored.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `ConfirmationId` | uuid NOT NULL | FK `AppointmentConfirmations` ON DELETE CASCADE |
| `TokenHash` | bytea NOT NULL UNIQUE | SHA-256 of the token; the token exists only in the e-mail |
| `CreatedAt` | timestamptz NOT NULL | |

A token works while its confirmation is open (neither confirmed nor closed) and before
`ExpiresAt`; a confirmed confirmation still answers its own tokens with "confirmed".

### `BookingPolicies`: two new columns

- `AutoCancelMode text NOT NULL DEFAULT 'after_email'`, check in (`off`, `after_email`,
  `before_session`).
- `AutoCancelHours integer NOT NULL DEFAULT 1`, check between 1 and 168.

A provider without a row uses the defaults.

### `Reminders`: used as the patient e-mail outbox

- `TemplateKey`: `confirmation`, `reminder_1h` or `cancellation`.
- New column `Attempts integer NOT NULL DEFAULT 0`; after 3 failed attempts the row becomes
  `failed`.
- New column `ConfirmationId uuid NULL` (FK `AppointmentConfirmations` ON DELETE SET NULL):
  the confirmation whose link the e-mail carries, if any.
- Nothing about the message is stored on the row: the worker reads the appointment, patient
  and provider at send time, so a reschedule or a changed e-mail address is always reflected,
  and creates the link's token then.
- The existing `Reminders_due_idx` on `ScheduledFor` serves the worker.

### `Notifications`: provider bell

- `Kind`: `appointment_confirmed` or `appointment_auto_cancelled`; `SubjectType`
  `appointment`; `SubjectId` the appointment id; `Channel` `in_app`; `ScheduledFor` and
  `SentAt` = the event time; `DeliveryStatus` `sent`; `ReadAt` set when read.

## 4. Server

New folder `Notifications/`.

### `ConfirmationRules` (pure)

`DeadlineAt(mode, hours, sentAt, startsAt)`: `off` → null; `after_email` →
`sentAt + hours`; `before_session` → `startsAt − hours`; then capped at `startsAt`; null if
the result is not after `sentAt`. The worker calls it when the confirmation e-mail is sent,
with the provider's current policy. `ReminderAt(startsAt, queuedAt)`: `startsAt − 1 h`, or
null if that is not after `queuedAt`.

### `AppointmentNotifications` (called from `Appointments.cs`, same `SaveChanges`)

- `OnCreated` / `OnRescheduled`: close any open confirmation; cancel pending reminders; if the
  patient has a `ContactEmail`, create a confirmation, queue `confirmation` now and
  `reminder_1h` at `ReminderAt` (both linked to it).
- `OnConfirmed(by)`: set `ConfirmedAt`/`ConfirmedBy` on the open confirmation; when
  `by = patient`, add an `appointment_confirmed` notification.
- `OnCancelled`: close the open confirmation; cancel pending reminders; queue `cancellation`
  now if the patient has an e-mail.
- `OnLeftActive` (`attended`, `no_show`): cancel pending reminders.

`Appointments.CreateAsync`, `UpdateAsync` and `SetStatusAsync` call these, so the existing
overlap handling and transactions stay as they are. Moving to `confirmed` through
`SetStatusAsync` (provider) calls `OnConfirmed(provider)`.

### `AppointmentNotificationWorker` (`BackgroundService`)

Every 60 seconds, each step in its own transaction, rows taken with `FOR UPDATE SKIP LOCKED`:

1. **Automatic cancellation:** open confirmations with `DeadlineAt <= now` whose appointment is
   still `scheduled` → appointment `cancelled` (`CancelledBy = 'system'`,
   `CancellationReason = 'Não confirmada no prazo'`), then `OnCancelled`, plus an
   `appointment_auto_cancelled` notification.
2. **Send due e-mails:** `Reminders` with `DeliveryStatus = 'pending'` and
   `ScheduledFor <= now`, in batches of 50. A `reminder_1h` whose appointment is no longer
   `scheduled`/`confirmed` becomes `cancelled` instead. A row with a `ConfirmationId` gets a
   fresh token (an `AppointmentConfirmationTokens` row) when its confirmation is still open;
   a reminder whose confirmation is already confirmed goes without a link. Success → `sent`,
   `SentAt`; for a `confirmation` e-mail, the confirmation's `DeadlineAt` is set from
   `ConfirmationRules.DeadlineAt` in the same transaction. Failure → `Attempts + 1`, retried next run (with a new token), `failed` at 3.
   A patient whose `ContactEmail` was removed meanwhile → `cancelled`.

Uses `TimeProvider` (registered as `TimeProvider.System`) so tests can move time. The link base
is `PatientPortal:BaseUrl`, required at startup when an e-mail sender is configured. Disabled
when `Notifications:WorkerEnabled` is `false` (tests run the steps directly).

### E-mail templates

Portuguese, plain and HTML, built like `PatientInvitations.Compose`; time shown in the
appointment's `Timezone`.

- Confirmation: subject "Confirme sua consulta com {profissional}"; date, time, modality, a
  "Confirmar presença" link and, when there is a deadline, "Sem confirmação até {hora}, a
  consulta será cancelada."
- Reminder: subject "Sua consulta com {profissional} começa em 1 hora"; same data, plus the
  link if unconfirmed.
- Cancellation: subject "Consulta cancelada"; date, time and provider.

### API

| Route | Auth | Result |
|---|---|---|
| `GET /api/public/appointment-confirmations/{token}` | none | `200 { providerName, startsAt, endsAt, timezone, modality, status }` with `status` `pending`, `confirmed` or `invalid`. Reading never confirms. |
| `POST /api/public/appointment-confirmations/{token}` | none | `204` confirmed (idempotent if already confirmed by this token); `410 { message }` when the link no longer works (expired, superseded, cancelled, unknown). |
| `POST /api/phi/patients/me/appointments/{id}/confirm` | patient cookie | `204`; `404` unless the appointment belongs to a record linked to the account; `409` unless it is `scheduled` and in the future. |
| `GET /api/phi/providers/me/notifications?page=1` | provider cookie | `{ items: [{ id, kind, appointmentId, patientName, startsAt, createdAt, readAt }], unreadCount, page, pageSize }`, newest first, 20 per page. |
| `POST /api/phi/providers/me/notifications/{id}/read` and `/read-all` | provider cookie | `204` |
| `GET` / `PUT /api/phi/providers/me/booking-policy` | provider cookie | `{ autoCancelMode, autoCancelHours }`; PUT validates mode and 1–168 hours and upserts the row. Only these two fields are exposed. |

Public routes are rate-limited per IP (ASP.NET Core rate limiter, 30 requests per minute). The
token is opaque and carries no patient data, so it may sit in the URL, as invitation tokens do.
The appointment list item (`GET /providers/me/appointments`) gains `confirmationDeadlineAt`
(open confirmation's `DeadlineAt`, or null) and `patientHasEmail`.

## 5. Frontend

### Patient app

- Public route `/patient/confirmar/{token}` (like `convite.$token.tsx`): shows provider, date,
  time and modality and a "Confirmar presença" button; then "Presença confirmada". Invalid
  link: "Este link não é mais válido." with a pointer to the provider.
- Portal sessions list: a "Confirmar" button on upcoming `scheduled` sessions.

### Provider app

- Calendar details panel: "Aguardando confirmação até {hora}" when there is a deadline;
  "Paciente sem e-mail: não receberá confirmação." when `patientHasEmail` is false.
- Phase B: the top-bar bell opens a list of notices with a badge for `unreadCount`
  ("{paciente} confirmou a consulta de {data}" / "Consulta de {paciente} em {data} cancelada
  por falta de confirmação"), each linking to the calendar day; "Marcar todas como lidas". The
  list refreshes every 60 seconds while the app is open.
- Phase B: Configurações → "Confirmação de consultas": rule (desligado / horas após o e-mail /
  horas antes da sessão) and number of hours.

All strings in `pt.json`.

## 6. Testing

- Unit: `ConfirmationRules` (each mode, cap at start, already-past deadline, short-notice
  reminder); request validation for the booking policy; e-mail templates contain no clinical
  data and show the appointment's time zone.
- Postgres (shared AppHost fixture), with a fake `TimeProvider` and a recording
  `IEmailSender`:
  - create queues a confirmation e-mail and a reminder; no e-mail and no deadline without
    `ContactEmail`; the deadline is set only once the confirmation e-mail is sent, so an
    unsent or failed e-mail never leads to an automatic cancellation;
  - reschedule closes the old confirmation and queues new ones; the old token returns `410`;
  - provider "Confirmar" closes the deadline without a notification; patient confirmation adds
    one;
  - worker: deadline passes → appointment cancelled by `system`, cancellation e-mail queued,
    notification added, slot free; reminder for a cancelled appointment becomes `cancelled`;
    send failure retries and becomes `failed` at 3; no plain token is stored anywhere (only
    hashes); a reminder after confirmation has no link;
  - provider scoping on notifications and booking policy; the portal confirm route refuses an
    appointment of an unlinked record.
- HTTP: public GET never confirms; POST with valid, unknown, superseded and cancelled tokens;
  401s on the protected routes.
- Browser (manual, Mailpit): create an appointment, open the e-mail, confirm on the page, see
  the bell; let a deadline pass and see the automatic cancellation; reminder arrives 1 hour
  before.
