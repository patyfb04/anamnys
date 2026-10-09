# Known gaps

**Started:** 2026-10-03 · **Last updated:** 2026-10-07
**Status:** Open. The official backlog is the Notion page "Plano de Implementação"; move
these items there when the workspace has free blocks, then delete this file.

Covers what is still missing after `2026-09-27-patient-list`, `2026-10-01-patient-records`,
`2026-10-02-patient-accounts`, `2026-10-03-portal-invitation`,
`2026-10-04-patient-portal-sessions`, `2026-10-05-provider-calendar` and
`2026-10-07-appointment-notifications`. New gaps found during
feature work are appended to the matching section.

## Pacientes

- **Patients with appointments cannot be deleted.** Any appointment, even a cancelled one,
  makes a patient undeletable (`PatientRecords.HasClinicalRecordsAsync` counts appointments),
  so archiving is the only option left.
- **Archiving leaves future appointments live.** An archived patient keeps their future
  appointments in their slots, and the portal still shows them as the next session.
- **Mobile check** of the provider-side "Portal do paciente" block and the invitation page at
  390px (the portal session pages were checked).

## Calendário

### Next deliveries (from `design/specs/2026-10-05-provider-calendar-design.md`)

- **Delivery 2 — availability:** weekly working hours (`AvailabilityRules`) and blocks,
  vacation and holidays (`AvailabilityExceptions`) shown on the grid (the mockup's "Lunch
  Break").
- **Delivery 3 — recurrence:** weekly and fortnightly series (`AppointmentSeries`).
- **Delivery 4 — patient self-booking:** free slots, holds (`BookingHolds`),
  `BookingPolicies` and `ServiceOfferings` (the mockup's "All Session Types" filter and
  prices).
- **Delivery 5 — Google Calendar sync** (`CalendarConnections`; the mockup's "Synced with
  Google Calendar").
- **Month view** was left out of delivery 1.

### Behaviour to revisit

- **Undoing "Realizada" may leave `LastVisit` stale.** When no other attended visit exists,
  `LastVisit` stays on the undone date (spec section 3 semantics).
- **"Realizada" creates no clinical `Sessions` row.** `Appointments.SessionId` stays empty;
  linking an appointment to its clinical session belongs to the notes work (see "Notas
  clínicas").
- **Appointments crossing midnight** are drawn only on their start day and cut at 24:00;
  one that started the day before the visible range is returned but not drawn.
- **Time zone:** every appointment is created in `America/Sao_Paulo` because `Providers` has
  no time zone column. `zonedToInstant` corrects the offset once, so it can be an hour off
  inside a DST transition, and `offsetLabel` prints half-hour zones as "GMT+5.5". Harmless
  while everything is São Paulo (no DST since 2019).
- **No drag and drop**: rescheduling is done in the details panel.

### UI polish

- **Accessibility:** the Dia/Semana toggle has no `aria-pressed`, today's header no
  `aria-current="date"`, the modality icon no `role="img"`; the patient search field and the
  cancellation reason have no label; creating by clicking the grid is mouse-only (the "Nova
  consulta" button is the keyboard path); cancelled cards have low contrast.
- **Grid layout:** with a visible scrollbar (Windows) the header columns drift from the body
  columns (`scrollbar-gutter: stable` on both); while a new week loads, the previous week's
  appointments can briefly stretch the hour range.
- **Form errors:** a 400 on a field with no visible error slot (time, modality) shows
  nothing; status changes show a generic toast for 400/422 instead of the server message;
  an empty duration field becomes 0.
- **Mobile tab bar:** with the new "Agenda" tab there are four tabs, so the raised "Nova nota"
  button is third of four instead of centred.

### Verification and tests

- **Manual browser check of delivery 1 is still pending:** create, reschedule and every status
  action; the "Horário já ocupado." toast; cancel then rebook the same slot (the new card must
  be clickable); "Realizada" updating "Último atendimento"; day view and the "Agenda" tab at
  390px; the appointment showing in the patient portal and the patient list.
- **Tests to add:** exact boundaries (42-day range, 500-character reason, empty patient id);
  success paths for Confirmar, Falta and Falta → Agendada; another provider's patient through
  the list join; the HTTP list filtering by repeated `status` keys.
- **Flaky test:** the first full suite run on the calendar branch had one failure whose name
  was not captured; five later runs passed. If it recurs, keep the full log and name it.
- **Hardening:** `AppointmentStatus.All` and `AppointmentRules.Modalities` are public mutable
  arrays; the `LastVisit` update has no concurrency guard.

## Notificações

Shipped in `2026-10-07-appointment-notifications`: e-mail confirmation with a one-time link
(public page and portal "Confirmar" button), configurable automatic cancellation, reminder 1h
before, cancellation e-mails, the provider's in-app bell and the "Confirmação de consultas"
setting. Pending: the manual browser check with Mailpit (create, confirm through the link,
automatic cancellation past the deadline, reminder, "Paciente sem e-mail" hint, portal button).

- **Dev worker acts on test leftovers.** `Notifications:WorkerEnabled` is `false` in tests, and
  the tests quiesce their appointment and outbox rows when they dispose. Leftovers only remain
  if a test process is killed before dispose; with the dev AppHost running, the worker would
  then process them (test providers only): it may cancel their appointments and try to send
  their e-mails.
- **Row lock held during the HTTP send.** The worker keeps the reminder row (and its
  transaction) locked while it calls the e-mail provider, so a provider edit of the same
  appointment waits for the send to finish.
- **Cancellation e-mail without a prior notice.** A cancellation e-mail is queued even when the
  patient was never e-mailed about the appointment (for example it was cancelled before the
  confirmation e-mail went out).
- **Undoing Confirmada to Agendada leaves the old link "invalid".** The portal's "Confirmar"
  button still works, but the e-mailed link no longer does until a new confirmation is issued.
- **Adding an e-mail to a patient later queues nothing.** Existing appointments of a patient who
  gets an e-mail address after being booked receive no confirmation or reminder.
- **Confirmation token in the API URL path.** The token travels in `/api/.../confirmations/{token}`
  and so lands in OpenTelemetry spans (and any access log). Redact it in the telemetry or move
  it to the request body.
- **Time zone and clock assumptions.** `NotificationBell` assumes America/Sao_Paulo, and the
  provider notification endpoints use `DateTimeOffset.UtcNow` instead of `TimeProvider`.
- **Rate limiter needs ForwardedHeaders.** The confirmation endpoints' limiter is keyed on
  `RemoteIpAddress`; behind a proxy it needs `ForwardedHeaders` configured before any
  production deploy, or every client shares one bucket.
- **Production must set `PatientPortal__BaseUrl`.** The server refuses to start when an e-mail
  sender is configured and no base URL is set.

Left for later:

- **WhatsApp channel.** Delivery 1 sends e-mail only, but reminders are modelled per channel
  (`Reminders.Channel`) so WhatsApp can be added without rework. Needs: a provider (Meta
  WhatsApp Business Cloud API or a BSP such as Twilio) and a processor agreement under the
  LGPD, since provider name, date and time leave the server; message templates approved by
  Meta; a way to enter the patient's phone (`Patients.Phone` exists but no screen fills it);
  the patient's opt-in consent; and a per-patient channel choice.
- **SMS** is also allowed by `Reminders_Channel_ck`; not planned.
- **E-mail to the provider.** The provider is notified only through the in-app bell; an
  optional e-mail digest was left out.
- **Patient self-booking** does not exist yet (calendar delivery 4). The notification flow is
  triggered on any appointment creation, so it will apply there too, but the self-booking
  screens themselves must still confirm by e-mail (decided 2026-10-06).
- **Production e-mail** is still blocked by the unverified Resend domain (see "Contas e
  e-mail").

## Videochamada

Not built yet; planned for after the calendar deliveries.

- **What the schema already has:** `RoomSessions` with a unique `AppointmentId` (one room per
  appointment), `StartedAt`, `EndedAt`, `RejoinCount` and `RelayUsed`. No join link is stored;
  it would be derived from the appointment or room and only open to that appointment's
  provider and patient.
- **Scope to design:** a "Entrar na chamada" action for `online` appointments in the
  calendar's details panel and in the patient portal; when the room opens and closes; what
  happens on rejoin.
- **Constraint:** the call carries patient data, so it must be self-hosted (media server and
  TURN relay included). Google Meet, Zoom or another hosted service is not an option without
  a processor agreement under the LGPD.
- **Link with notes:** a call is where audio for transcription would come from, so this work
  meets "Notas clínicas" through the clinical `Sessions` row.

## Notas clínicas

- **Notes.** "Notas recentes" on the record links to the note editor, still a placeholder;
  `PatientNotesView` (`/patients/$id/notes`) calls `notesApi` routes that do not exist.
- **Appointment → session → note chain is unbuilt.** The schema links them through
  `Appointments.SessionId` and `Notes.SessionId` (and `Transcripts.SessionId`); nothing
  creates a `Sessions` row yet.

## Portal do paciente e convites

- **Invitations:** no re-send rate limit; no reminder before expiry; the registration form
  does not prefill the invited email.
- **Dev only:** patient registration must start from the patient app origin (5274); from
  5273 Keycloak refuses the `redirect_uri`.

## Contas e e-mail

- **Email in production.** Verify a sending domain in Resend and configure `Email__*` in the
  deployed environment. Keycloak has no production SMTP either: account verification and
  password reset do not work outside dev. Until a domain is verified, Resend's sandbox only
  delivers to the account owner's own address.
- **Login theme hides Keycloak's global messages** (found 2026-10-08). No page nor
  `Template.tsx` renders `kcContext.message`, only per-field errors, so errors Keycloak sends
  as a page message — a rejected new password (password policy), a temporary lockout — show
  nothing and the page just reloads. Render `kcContext.message` (error/warning/info) in
  `Template.tsx`.
- **2FA with several authenticators breaks login** (found 2026-10-08). `LoginOtp.tsx` does
  not render the credential selector (`otpLogin.userOtpCredentials` / `selectedCredentialId`),
  so Keycloak always checks the code against the first OTP credential. The forgot-password
  flow asked to scan a new QR code and added a third OTP credential instead of replacing the
  old ones, so the phone's current code never matched. Fixed by hand for the dev account
  (old OTP credentials deleted); still to do: the selector in `LoginOtp.tsx`, and decide
  whether the reset flow should replace the existing OTP instead of adding one.
- **OTP policy:** all three realms use `HmacSHA256`; the running providers realm also has
  `otpPolicyLookAheadWindow: 0` (the realm files do not set it). Google
  Authenticator and some other apps ignore the algorithm and generate SHA1 codes, and a zero
  window fails codes typed near the end of their 30 s. Consider `HmacSHA1` with look-ahead 1
  (Keycloak's defaults); changing the algorithm invalidates existing authenticator setups.
- **Password history removed from the providers realm** (2026-10-08, user's decision): the
  policy is now `length(12) and notUsername and notEmail`. The owners realm still has
  `passwordHistory(10)`.

## LGPD e segurança

- **Patient rights (LGPD).** The portal cannot list or remove provider links, and the
  patient cannot delete their account. `Patients.AccountId ON DELETE SET NULL` is ready.
- **PHI read logging.** `AccessLogs` exists; nothing writes to it. Appointment changes are
  not audited either (`AuditEntries` requires a note or document).

## Qualidade

- **No frontend automated tests**; UI is verified manually in the browser only.
- **`anamnys-aspire.Server.http`** has no requests for the appointment or patient endpoints.

## Documentação

- **Notion "Modelo de Dados":** add `PatientAccounts` (split from `Patients`),
  `PatientDiagnoses`, `PatientInvitations` and `Patients.ArchivedAt` / `ContactEmail`.
  Blocked by the workspace's block limit.
- **`anamnys-aspire/CLAUDE.md`** frames HIPAA/BAA as hard requirements; the product operates
  under the LGPD (processor agreements, art. 39). Align the wording.
- **`anamnys-aspire/CLAUDE.md` route tree gotcha is stale:** it says each app's
  `src/routeTree.gen.ts` must stay committed, but `.gitignore` ignores `**/src/routeTree.gen.ts`
  and no app tracks one. Decide which is right and align the other.

## Concluído

- **Patient portal (delivery 3):** done in `2026-10-04-patient-portal-sessions`.
- **Provider app accepted a patient-only session:** fixed in `fix/realm-scoped-session`.
- **Appointments had no write path:** done in `2026-10-05-provider-calendar`.
