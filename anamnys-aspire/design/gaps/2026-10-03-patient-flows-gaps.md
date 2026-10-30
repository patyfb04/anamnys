# Known gaps

**Started:** 2026-10-03 · **Last updated:** 2026-10-26
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

Psychological documents and the CFP 006/2019 validator (F-05 deliveries 1 and 2, all six kinds;
chapter `documentation/22-psychological-documents.md`).

- **A parecer about an institution or a person who is not a patient is not supported.** The parecer
  is bound to a patient like every other document, and the subject named in it is that patient
  (art. 14 allows an institution or another person as the subject).
- **Co-authors of the relatório multiprofissional have no account and do not sign.** The team is
  typed text (name, category, registry, analysis) inside the provider's document; only the
  psychologist signs in Anamnys. Co-authors with an account or their own signature are not built.
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

## Temas

Theme vocabulary (F-02; chapter `documentation/23-theme-vocabulary.md`).

- **No panels yet.** The tags are read only by the note search; the longitudinal panels
  (F-10 onwards) are not built.
- **Suggestion cuts long text.** A note that does not fit the model context is cut to a prefix;
  themes mentioned only in the cut part are not found.
- **One vocabulary for all providers.** Maintained by staff; providers cannot define their own
  terms, and only mental health is seeded.
- **Inference is only partly serialized.** Theme suggestions queue behind each other (one at a
  time), but suggesting while a note is structured runs the structuring context and the cached
  suggestion context (≈ 0.6 GB, held for the process) side by side.
- **Suggestion latency (improved 2026-10-23).** With the prefix cache and a 4096-token context a
  one-paragraph note takes ≈ 4–5 s on the dev CPU (was ≈ 40 s). The model and the prefix are now
  warmed in the background after server start and after every vocabulary change
  (`NoteStructuring:WarmSuggestionOnStart`), so the first suggestion normally takes the same ≈ 5 s;
  one requested before the warm-up ends waits for it (the dialog says so after 8 s). Long notes cost prefill time in proportion to their
  length (no measurement beyond one paragraph yet).
- **The tests create Keycloak users `test.ops` and `test.owner`** in the dev owners realm; they
  persist there.
- **Manual browser check pending** for the "Temas" block (chips, picker, suggest dialog, archived
  patient), the "Tema" filter and the admin `/vocabulary` page; so are the new `.http` requests
  against a live AppHost.

## Trechos de origem

Excerpt references for theme tags (F-03; chapter `documentation/25-excerpt-references.md`).

- **Locating is slow and runs in the background.** Measured warm on the dev CPU: suggest (codes only)
  4 to 4.5 s; background locate about 6 s for one tag and 8.5 to 9.6 s for 2 to 3 tags; structuring
  proposals with quotes 9.5 to 11.5 s. Output caps are per mode: 160 tokens for codes, 720 for quotes.
- **A locate holds the suggester's shared gate.** Suggestions and other locates wait behind it.
- **Queued locates are lost on restart.** The queue is in memory; the tags stay `section` until the
  provider uses "Localizar trechos".
- **Proposals whose quote is not found stay `section`** until "Localizar trechos" is used; nothing
  retries them by itself.
- **A section chosen by the provider restricts the locate to that section.** An explicit choice wins,
  so a passage the model finds elsewhere is not used (fewer exact upgrades).
- **Quotes must be literal.** A paraphrase by the model is not accepted; the reference stays `section`.
- **The migration deleted the old `ExcerptRefs` rows once** (they were unusable and nothing created them)
  and gave the 114 active tags of the dev database a `section` reference. Existing tags are not located
  by the model in bulk.
- **Note sections and document pages are the only sources.** Documents (F-08) are sources through
  `SourceType` `document` with a `PageNumber`; transcripts and patient quotes (F-15) have no `SourceType`
  yet.
- **The architecture test is file-level.** It checks that a class in `Analyses/` calls `RequireExcerpt`,
  not that every query in it does; `GetPassageAsync` shows an exact range only when the text at the offsets still equals the quote.
- **A restructure (draft) relocates the references and queues a note-wide locate.** `NoteStructuringJob` calls `RelocateNoteAsync` after replacing the sections: a quote not found again, or a section key that is gone, leaves a `section` reference on the first non-empty section (the quote is kept) until the locate finds it.
- **The F-60 export writes theme tags without section or quote** (to add later).
- **No frontend automated tests for highlight/selection.** The highlight, scroll, `?excerpt=`, the
  "Marcar tema neste trecho" selection and the polling are verified in the browser only; the manual
  check is pending.

## Documentos recebidos

External documents (F-07; chapter `documentation/26-external-documents.md`).

- **The F-60 export does not include external documents** (files and metadata).
- **The memory peak while ClamAV reloads signatures was not measured**; expect up to about 2x the idle 969 MiB.
- **Signature updates need outbound internet** from the ClamAV container (`database.clamav.net`, HTTPS).
- **Uploads are held in server memory** (up to about 21 MB each) so the file never reaches a temp file.
- **Not exercised in a browser:** the upload (multipart header handling) and the open-in-new-tab flow.

## Fatos declarados

Document reading and declared facts (F-08; chapter `documentation/27-document-facts.md`).

- **Recall is uneven.** In the probe on the six sample PDFs, issuer, registration and date were often
  missed when they sit at the end of a short document. Some kinds were wrong (a therapy filed as
  `medication`) and exam results came out as paraphrased `conclusion` values. Every kept quote was
  literal and located; the provider's review is the safeguard. Prompt and model tuning is open.
- **Extraction is slow on CPU:** 47 to 90 s per one- or two-page document, one at a time, so a batch queues
  for minutes. A suggestion meanwhile takes 5.8 to 7.0 s instead of 4.0 to 4.3 s (priority gate).
- **Linux is untested** for Tesseract.CrossPlatform (young, single-maintainer package; may need
  `libgomp1`). A Linux container OCR smoke test is required before deploy. The first OCR needs outbound
  access to raw.githubusercontent.com, or the verified data file placed beforehand.
- **One re-read case is not recovered by the restart sweep:** a "Ler de novo" extraction interrupted after
  an earlier successful one. Pressing the button again recovers it.
- **"Confirmar todos" is sequential:** one request per fact; a fact answering `not_pending` is skipped,
  any other error stops it; there is no bulk endpoint.
- **Extraction attempt guard:** each extraction counts an attempt (`ExtractAttempts`) before the model
  runs; the startup sweep skips a document with 2 or more since its read, leaving its pending facts as they
  are, so an extraction that crashes the process is not retried forever. A new read and "Ler de novo"
  reset the count.
- **Decompression cap:** a JPG/PNG whose header declares more than 50 MP is refused (read `failed`) before
  any decode; large JPEGs are decoded at a reduced scale.
- **Deferred: no hang watchdog on the read path.** An OCR, PDF render or extraction call that never
  returns (without crashing) holds its worker until the process restarts.
- **Facts of a deleted document are removed by design** (every status, confirmed included, and their
  references), audited as `facts_deleted`. A confirmed fact cannot outlive its document.
- **The extraction queue is in memory** and is rebuilt at startup by the sweep (documents never extracted
  or re-read after their extraction).
- **Pages beyond the 20th are not read;** the document is flagged `PagesTruncated`.
- **The F-60 export does not include external documents or declared facts.**
- **Not exercised in a browser:** the facts modal, passage modal and declared-facts card are type-checked
  and built only; tests use a fake extractor.

## Linha do tempo de medicação

Medication timeline (F-09; chapter `documentation/28-medication-timeline.md`).

- **A stop never shows "no prontuário".** A suspension closes the target record and creates none, so its
  fact has no link to show; the fact stays confirmed and can be applied again to another target.
- **The prefill is text rules only**, no model. Unusual wording leaves fields blank, and a quote naming
  two drugs is not split into two records.
- **The timeline is not in the Análises page yet.** It lives on the patient record only; F-10 adds the
  other layers (events, themes, notes) and may move it.
- **A record from a deleted document keeps only the label** "documento excluído"; the link to the
  document and the passage are gone.
- **One record per fact:** a second `new` or `dose_change` from the same fact answers 409
  `already_recorded`; fixing a wrong record goes through the existing edit flow on the medications card.
- **Not exercised in a browser:** the medication form, the timeline card and its details panel are
  type-checked and built only.
- **The F-60 export does not include external documents,** so the origin of a document-based medication
  is not exported either.

## Comparecimento

Attendance statistics (F-04; chapter `documentation/29-attendance-stats.md`).

- **No duration line.** The statistics use dates only; session length is not shown or computed.
- **Appointment invalidation is by prefix.** The appointment hooks do not know the patient id, so a change
  refreshes the attendance and timeline queries of every patient in the cache.
- **Sessions marked `scheduled` with no appointment are ignored.** They are treated as a plan; only
  `attended` sessions without an appointment count.
- **No practice-wide view until F-10.** The card is per patient; the Análises page will reuse the same
  computation.
- **Not exercised in a browser;** the real-database tests were compiled but not run (Aspire was running).

## Análise do caso

Case analysis (F-10 + F-11; chapter `documentation/31-case-analysis.md`).

- **No note passage modal.** The cell modal shows the quote stored in the excerpt reference and links to the
  note, where the passage is highlighted in place; there is no modal with the passage in its note context.
- **No practice-wide view.** The page is per patient; nothing aggregates across patients.
- **No export of the analysis.** The grid, pairs and timeline cannot be saved or printed from the page.
- **Not exercised in a browser;** the page is type-checked and built only (build and lint).

## Ausência de temas

Theme absence (F-12; chapter `documentation/32-theme-absence.md`).

- **The modal opened from the card has no "na mesma sessão" list.** The card carries only the absence and its
  last passage, not the other terms of that session; the modal also reads "Registrado em" with the date of the
  last record.
- **No notifications, by decision.** Absence only shows on the Análises page; nothing is sent or listed in the
  notification centre.
- **`daysSince` counts to today,** not to the latest signed session, so the number keeps growing while the
  patient has no new session.
- **Per term as recorded.** A renamed or split theme is not followed to its successor; the old term is excluded
  (superseded) and the successor is judged on its own records.
- **Not exercised in a browser;** the card, the lane mark and the settings section are type-checked and built
  only (build and lint).

## Trajetória de escala

Scale trajectory (F-13; chapter `documentation/33-scale-trajectory.md`).

- **No PDF export for operadoras yet.** The chart cannot be saved or printed from the page.
- **Notes need at least 6 signed sessions.** They come from the case analysis, so with fewer sessions the
  "Notas" toggle has nothing to show.
- **No frontend unit tests.** The provider app has no test runner; the model is kept pure for when one is added.
- **Not exercised in a browser;** the card is type-checked and built only (build and lint).

## Guarda e descarte

Retention and compliance panel (F-06; chapter `documentation/30-retention-panel.md`).

- **The discard dialog shows categories, not counts.** The server knows the counts only after the discard,
  so the dialog lists what is deleted and what is kept; the exact counts are on the disposal term.
- **Export ZIPs made before a discard keep the record** until they expire (`ExportSweep`), and so does any
  copy the provider downloaded. The discard does not touch them.
- **The Keycloak user of a patient portal account is not removed.** Only the `PatientAccounts` row is
  deleted, and only when no other provider's patient row uses it.
- **The guard rule per record kind is not used beyond `prontuario`.** `RetentionRules` can hold other
  kinds, but the clock reads only the `prontuario` row.
- **The per-note and per-document audit rows of a discarded patient are deleted** (they hold clinical text
  and have restrict foreign keys). Only the `patient_discarded` row (ids and counts) and the provider-level
  rows remain.
- **No e-mail alerts, by decision.** Deadlines and unsigned notes reach the provider only through the bell
  and the page.
- **Not exercised in a browser;** the page, dialog and settings section are type-checked and built only.

## Exportação

Full account export (F-60; chapter `documentation/24-account-export.md`).

- **F-57 and F-56 own the 60-day window and the deletion.** D-08 promises the export for 60 days after
  cancellation, and (Terms 10.3, FAQ) after a trial ends or an account is suspended, then deletion; only
  active accounts can export today, and nothing deletes account
  data.
- **The downloaded ZIP leaves our control.** It holds patients' health data, is not encrypted and has no
  password; the screen only warns the provider.
- **Whole-account text in memory.** The text of the entire account is loaded before the ZIP is written
  (PDFs are streamed); a very large account costs memory in proportion. Not measured.
- **Single instance assumed.** The queue is in-process and the restart sweep fails every queued or
  running export, like `OrphanNoteSweep`; with several instances it would fail another one's job. A
  shutdown also leaves a running export until the next start.
- **Retention is 7 days**, swept hourly; after that the provider generates a new export.
- **Excluded on purpose:** voice transcripts, the original typed text and audit values. Pending AI theme
  proposals are included and flagged. A signed document whose PDF is neither stored nor renderable shows
  "PDF indisponível" (text kept in the JSON and HTML).
- **Termos:** cláusula de preservação após suspensão ainda com [60] entre colchetes — decidir junto
  com F-56.
- **Manual browser check pending** for the "Exportar meus dados" section (request, polling, download,
  retry, expired, bell link); so are the new `.http` requests against a live AppHost.

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
