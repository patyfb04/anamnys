# Known gaps

**Started:** 2026-10-03 · **Last updated:** 2026-10-18
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
- **Record actions from the reference mockups not built** (2026-10-12 redesign kept only working
  features): document upload, patient dossier, clinical theme progression chart. "Agendar
  consulta" only opens the calendar.

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

- **Live session ideas from the reference mockups not built:** timestamped bookmarks during
  recording ("Mark Clinical Moment"), session consent instrument and audio-destruction record
  (flow 04, blocked by D-03), billing procedure target (D-04), named-entity redaction (F-27),
  extra note formats (intake, custom evaluation).

- **Notes.** `/note-editor` lists all of the provider's notes; `PatientNotesView`
  (`/patients/$id/notes`) uses `POST providers/me/notes/search`.
- **Appointment → session → note chain.** Finalizing a live recording creates the `Sessions`
  row (`attended`, `ActualStart`/`ActualEnd`) and links it to the appointment
  (`Appointments.SessionId`) and the note (`Notes.SessionId`). Typed notes also link (optional
  appointment), and both paths move the appointment to attended. `Transcripts.SessionId` is
  not wired.
- **Note structuring (live session delivery 2) is done**: the transcript is organised into the
  sections of the chosen format (DAP, SOAP, Biopsychosocial) and can be regenerated. Still missing:
  - Transcripts longer than the model context (`too_long`) are not chunked; they fail.
  - Note review and signing (delivery 3) is done: section edit, sign, audit (`AuditEntries`),
    row lock and edit refusal while structuring is pending. Still missing: audit history view;
    correction of a signed note through a new linked version; `SignedByCrp` is format-only (no CRP registry check).
  - PDF export is done (QuestPDF, SeaweedFS, 1 h presigned URLs). Still missing:
    - ICP-Brasil digital signature in the PDF (future).
    - Production storage host: a public TLS hostname with a reverse proxy to SeaweedFS's S3 port only. The code side is done: publish mode requires the AppHost parameter `storage-public-url`, and the server refuses to start outside Development unless `Storage:PublicUrl` is an absolute https URL.
    - Backup of the SeaweedFS volume (`anamnys-storage-data`).
    - QuestPDF Community license holds only below US$ 1M annual revenue.
    - The exported PDF is not yet sealed into `DocumentHashes` (F-45); its SHA-256 is stored in `Notes.PdfSha256`.
    - Test objects (`tests/{guid}.pdf`) accumulate in the dev bucket.
    - Manual browser check of export/download pending.    - The provider name on the PDF is the name at first export, not at signing.
    - `Storage` options are not validated at startup; empty settings fail only after the initializer's retries.
    - No endpoint-level test of the 409/503 export responses.
    - An unknown section key (e.g. `PtFunctional`) prints its raw key as the PDF section title.
    - The deployment image needs tzdata for `America/Sao_Paulo`.
    - Untested PDF cases: Biopsychosocial name, no birth date, unsigned note.
    - Objects written to the dev storage volume before `-s3.encryptVolumeData` stay unencrypted (recreate `anamnys-storage-data` to clear them).
    - SeaweedFS's S3 gRPC port (18333) still listens on all interfaces; only HTTP services are loopback-bound.
  - A replay after a lost commit acknowledgement can report Stale, AlreadyHasSections or
    Conflict even though the change was saved (rare; the startup sweep covers pending).
- **Hub progress sends are not best-effort**: a hub error while sending the "structuring"
  progress marks structuring as failed.
- **One structuring holds the single worker for minutes of CPU**: transcriptions of other
  notes (any provider) wait in the queue behind it. Consider a separate worker, or a priority
  for transcription.
- **The first structuring after each server start re-hashes the 4.7 GB model** (tens of
  seconds) before loading it, so that note waits longer.
- **Restructure format is written only on success** (fixed): the job carries the requested
  format and sets `Notes.Format` in the transaction that replaces the sections; a failed or
  discarded restructure keeps the old format. Signing also refuses (Locked) if section keys
  do not match the format.
- **Audit coverage boundary.** Audited: `ai_generated`, `section_edited`, `sections_created`,
  `signed`, `note_created` (typed notes and live finalize). Not audited: `RawTranscript` write, restructure
  request status change, pipeline status/failure changes.
- **Signed-note immutability and append-only `AuditEntries` are enforced by the application
  only** (no DB triggers).
- **Production memory sizing**: Whisper medium + Qwen2.5 7B Q4_K_M (~6 GB) + recording buffers
  must fit in the container; size it, or limit concurrency.
- **No `AccessLogs` writes for notes yet** (`AuditEntries` are written for note mutations).
- **Production model directory must be a persistent volume**, or each deploy re-downloads
  the Whisper model and the two Qwen GGUF files (~4.7 GB).
- **Production `LiveSession:ModelSha256` must be set before deploy.** The `medium` SHA-256
  in `appsettings.json` came from the Hugging Face LFS pointer and was not computed
  locally; verify it before production.
- **Live session has no automated frontend tests** (no runner); verified manually only.
- **No cap on concurrent live recordings per provider.** Each 90-minute job holds ~270 MB of
  buffer plus ~345 MB of samples in memory; size the container, or add a limit.
- **Hub connections are not closed when the session expires**
  (`CloseOnAuthenticationExpiration` is not set on the hubs).
- **`Sessions.ActualStart` resets to the reconnect time** if a recording begins again after
  its buffer expired or the server restarted.
- **`BeginRecording` does not check the appointment's status or date**; only the UI filters.
- **Pending: manual browser check of the note editor delivery** (as `dev.provider`):
  (1) sidebar "Editor de Notas" opens the list with "Pendentes" selected, each filter and the
  name search work, "Carregar mais" appears past 25; (2) "Nova nota" in AI mode with text lands
  on the note, shows "Organizando em seções…", then sections with the "Rascunho gerado por IA"
  badge and a collapsed "Texto original"; (3) manual mode gives empty sections, a non-AI badge,
  and editing and signing work; (4) "Nova nota" from a patient record preselects the patient;
  (5) from the calendar a past appointment shows "Escrever nota", preselects patient and
  appointment, and after creating it is "Realizada" and "Escrever nota" is gone; (6) a live
  session finalized on a started appointment moves it to "Realizada"; (7) at 390px the "Nova
  nota" tab opens the create page and the list and create page fit without horizontal scroll.
- **Typed-note creation and a lost commit acknowledgement**: after the retrying execution
  strategy re-runs, it can create a duplicate note (no appointment) or report a false 409 (with
  an appointment); same caveat as the other note writes.
- **The patient notes page (`/patients/$id/notes`) shows only the first 25 notes** (no
  "Carregar mais").
- **The archived-patient check at note creation does not lock the patient row**: a note created
  at the same moment as archiving can be written.
- **The mobile "Nova nota" tab no longer highlights on `/notes/$noteId`** (its prefix is
  `/note-editor`).
- **Clearing an invalid `appointmentId` from the create page URL skips the preselection** of
  today's appointment.
- **The `2026-10-10-live-session.sql` migration must run before the server starts**:
  `OrphanNoteSweep` fails startup otherwise.
- **Each test-suite AppHost run's `OrphanNoteSweep` fails in-progress live notes** in the
  shared dev database.
- **Lock order differs between typed-note creation and the confirmation paths**: creation locks the
  appointment, then closes its confirmations; the confirmation paths lock the confirmation, then the
  appointment. A deadlock is near impossible (confirming needs a not-yet-started appointment; the
  attended transition needs a started one); if it happened, PostgreSQL would abort one side (40P01)
  and the retrying strategy replays it.
- **`Appointments.SetStatusAsync` reads without a lock or concurrency token** (pre-existing): a
  calendar status change racing a typed-note creation can overwrite `attended`.
- **The live finalize does not check whether the appointment already has a note**, so a live
  recording on an appointment with a typed note adds a second note to the same session (extends the
  "`BeginRecording` does not check the appointment's status" gap).
- **Create page (`/note-editor/new`) polish**: a failed load of the preselected patient shows no
  message; the filter chip group's `aria-label` duplicates the h1; the mode cards have no arrow-key
  navigation; a disabled "Criar nota" gives no hint why.
- **The typed prompt's rules 3, 5 and 6 wording is not pinned by tests.**
- **Legacy `notesApi.get`/`update`/`export` and the old `Note` type still call routes that do not
  exist** (pre-existing, unused by this delivery).
- **The `.http` requests for the three new routes were not run against a live AppHost** (pending
  with the manual browser check).
- **Scales (F-01):**
  - No database trigger enforces the scale regimes (unavailable instruments, opt-in, archived
    patients); enforcement is in `ScaleRecording` only. Same family as signed-note immutability.
  - Opt-in cannot be revoked: `RevokedAt` is read but never written, and the unique key
    `(ProviderId, InstrumentId)` ignores `RevokedAt`, so it will block a new opt-in once
    revocation exists.
  - Multidimensional instruments (one application, several scores) do not fit
    `ScaleApplications."Score"`; F-67.
  - Scales recorded from the patient record are not linked to a session (only those recorded
    from the note page are).
  - The `InstrumentSeries` SQL view selects `RawAnswers` and ignores `RemovedAt`; nothing reads
    it. Drop or fix it before anything does.
  - A client clock ahead of the server is absorbed by a 5-minute tolerance (`AppliedAt <= now +
    5 minutes`, stored as `min(AppliedAt, now)`); a clock more than 5 minutes ahead still fails
    the check.
  - Removing a scale entry is allowed for an archived patient (same as the other clinical
    cards).
  - Manual browser check of the scales UI (card, modal, help, removal, archived patient) is
    pending, as are the new `.http` requests against a live AppHost.

## Documentos

Psychological documents and the CFP 006/2019 validator (F-05 delivery 1; chapter
`documentation/22-psychological-documents.md`).

- **Delivery 2 is REQUIRED to close F-05**: laudo, parecer and relatório multiprofissional (rulesets
  already seeded). Needs, from the spec's "Delivery 2 notes": the parecer confirmation text already
  in the catalog (`resultado_de_avaliacao_ou_intervencao_psicologica`); kind-aware confirmation
  texts, because `DocumentCatalog.Prohibition(code)` is kind-agnostic and shared codes read "do
  relatório"; and a CRP/co-authorship design for the Provider placeholders
  `nome_autor_inscricao_crp_e_titulacao`, `nomes_profissionais` and `inscricoes_profissionais`
  (art. 12 co-authorship model).
- **Delivery protocol** signed by the requester (art. 16 §1) is not built; it belongs to F-07.
- **Retention**: `ClinicalDocuments."RetentionUntil"` is never written (F-06).
- **`ConformityChecks."ForbiddenFound"` is always empty by design.** Prohibitions are explicit
  confirmations (and one by construction), not text detection. Do not fill it with guesses.
- **No endpoint-level tests** for the sign, locked and archived response bodies (the service is
  covered; only the 401 and routing are tested at the endpoint).
- **Typing during an in-flight save can be overwritten**: the editor applies the server response
  of a save over the form, so characters typed while a save is in flight may be lost.
- **An archived patient does not block save or sign** of an existing draft (only create and
  correct answer `409 archived`), like notes.
- **An abandoned correction draft cannot be discarded**, and the original shows "Substituído"
  meanwhile.
- **The editor uses the latest ruleset**, and `LatestRulesetAsync` has no tie-break on equal
  `EffectiveFrom`.
- **The atestado prints a "Paciente:" identification line above the continuous text.** Decide with
  legal review whether identification may sit outside the continuous paragraph (art. 10 §5º).
- **No database trigger** enforces signed-document immutability; it is in
  `ClinicalDocumentService` only (same family as signed notes and scales).
- **Manual browser check pending** for the card "Documentos", the editor (conformity panel, "?"
  help, sign in both modes, correction, PDF export and download), the Ajustes section and the
  "Cidade de atendimento" profile field; so are the new `.http` requests against a live AppHost.

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
- **Tests load `appsettings.Development.json`**, so the server under test downloads
  `ggml-base.bin` on the first transcription job if it is not in the model directory; this
  may slow CI.
- **`.gitignore` ignores `**/src/routeTree.gen.ts`**, contradicting the CLAUDE.md gotcha that
  it must stay committed (see also "Documentação"); one of them is wrong.

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
