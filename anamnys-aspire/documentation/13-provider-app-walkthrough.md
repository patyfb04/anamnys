# Chapter 13: Walkthrough — `apps/provider`

This is the clinical app — where a provider manages patients and (eventually) dictates,
reviews, and signs notes. It's also the frontend application with the most going on, which
makes it a good one to read closely — but it's important to be precise about *which* parts
are real and which are placeholders standing ready for backend work that hasn't happened
yet. Getting this distinction right here is a direct application of the habit Chapter 1
asked you to build.

## The route tree

```
_app.tsx                                    layout: auth gate + shell
_app/
├── patients/
│   ├── index.tsx                           patient list (+ create modal)
│   └── $patientId/
│       ├── index.tsx                       patient record
│       ├── notes.tsx                       patient's notes list
│       └── live-transcribe.tsx             live dictation session
├── notes/
│   └── $noteId.tsx                         note editor
└── settings/
    ├── index.tsx
    └── account.tsx
```

## What's actually built

`patients/index.tsx` is the patient list, backed end to end by
`POST /api/phi/providers/me/patients/search` (`anamnys-aspire.Server/Patients/`). Search,
filters (latest-note status, name, email, last/next visit ranges, archived), sortable
columns and paging all run server-side, scoped to the signed-in provider. The page renders
a table at `md+` and cards below (`components/patients/`), and keeps its filter state in
`hooks/usePatientSearch.ts`, never in the URL, because name and email are PHI. See
`design/specs/2026-09-27-patient-list-design.md`.

Creating a patient happens in `PatientFormModal`, a tabbed modal opened from the list
(basic data, then an optional clinical profile of diagnoses, medications and treatment-plan
objectives), saved in one transaction by `POST /api/phi/providers/me/patients`.
`patients/$patientId/index.tsx` is the patient record: edit basic data (same modal),
archive or reactivate, delete a record created by mistake (only when it has no clinical
history or portal login — Res. CFP 001/2009 retention), and add, edit, resolve/end or
remove clinical items in place (`components/patients/detail/`). Every route resolves the
patient with `Id == id && ProviderId == <session provider>`, so another provider's patient
is a 404. See `design/specs/2026-10-01-patient-records-design.md`.

Every provider record requires a **contact email** (`Patients.ContactEmail`), used for
scheduling notices only. It is deliberately not the portal login: that is
`PatientAccounts.Email`, on a separate table (Chapter 8), and a portal sign-up is never
attached to a provider record by matching emails. A record is linked to an account
(`Patients.AccountId`) only through an invitation the provider sends from the record's
*Portal do paciente* block, accepted by the account with the invited email
(`design/specs/2026-10-03-portal-invitation-design.md`).

Forms read field errors from `ApiError.errors` (`packages/shared/src/api/client.ts`), the
server's `ValidationProblem` body, and show them next to each field.

`patients/$patientId/notes.tsx` is a thin route wrapper around `PatientNotesView`, which
follows the same shape as the record page: a `useQuery` call, explicit loading and error
states, shared UI kit components (Chapter 12) and `useTranslation` for every piece of
user-facing text. `NoteCard` backs its list, and `AccountMenu` backs the account menu in
the app shell. The notes it lists come from `notesApi`, whose server routes do not exist
yet.

## Agenda

`calendar.tsx` is the provider's agenda, reached from the *Agenda* item in the side
navigation (always enabled). It is backed by four endpoints under
`/api/phi/providers/me/appointments` (`anamnys-aspire.Server/Appointments/`): list a date
range, create, reschedule, and change status. As everywhere in the `phi` group, the provider
comes from the session and every query filters by it, so another provider's appointment or
patient is a 404. See `design/specs/2026-10-05-provider-calendar-design.md`.

The page has a week view and a day view, with the grid in `components/calendar/`. On narrow
screens (`hooks/useIsMobile.ts`) it always renders the day view and hides the toggle. Its
state lives in the URL, unlike the patient list, because nothing in it is PHI: `view`
(`week` or `day`, default `week`), `date` (`YYYY-MM-DD`, default today in the practice time
zone) and an optional `status` filter. Data comes from `hooks/useAppointments.ts`, and every
mutation invalidates every `["appointments"]` query.

Clicking an empty cell opens the *Nova consulta* dialog prefilled with that day and the time
rounded down to 15 minutes; the header button does the same at 09:00 on today when today is visible, otherwise on the first visible day. Clicking a
card opens the details panel, which links to the patient record and offers the actions
allowed for the current status, plus *Editar*, which reopens the dialog in edit mode.
Status changes follow this table, enforced on the server:

| From | Allowed to |
|---|---|
| `scheduled` | `confirmed`, `attended`, `no_show`, `cancelled` |
| `confirmed` | `scheduled`, `attended`, `no_show`, `cancelled` |
| `attended`, `no_show` | `scheduled` (undo a misclick) |
| `cancelled` | nothing |

`attended` and `no_show` also require the appointment to have started. Cancelling records
who cancelled and an optional reason (up to 500 characters). Marking an appointment
*Realizada* sets `Patients.LastVisit` to the latest of its current value and the
appointment's start; moving it away from `attended` recomputes `LastVisit` from the patient's
remaining attended appointments, all in the same transaction as the status change. Only
`scheduled` and `confirmed` appointments can be rescheduled.

Two kinds of rejection reach the user. A `409` carries a Portuguese `message`, shown as a
toast: *Horário já ocupado.* for an overlap, *Esta alteração não é permitida no status
atual.* for a transition outside the table, and *A consulta ainda não começou.* for
`attended`/`no_show` on a future appointment. A `422` on create means the patient is
archived and shows *Paciente arquivado.* Validation failures (duration outside 5-480
minutes, unknown modality, an invalid range) are a `400` `ValidationProblem`, like the other
forms.

Overlap is not checked in application code. The database enforces it through the
`Appointments_no_overlap` constraint (`UNIQUE ("ProviderId", "Slot" WITHOUT OVERLAPS)`), so
two concurrent requests cannot both win; `Appointments.cs` only translates that constraint
violation into the `409`. Back-to-back appointments are allowed, and a cancelled appointment
frees its slot. Availability, recurrence, patient self-booking and Google Calendar sync are
not built yet.

## Confirmação e avisos

Every new or rescheduled appointment asks the patient to confirm by e-mail. The server code
is in `anamnys-aspire.Server/Notifications/`, the schema in
`design/migrations/2026-10-07-appointment-notifications.sql`, and the decisions in
`design/specs/2026-10-07-appointment-notifications-design.md`.

**Nothing is sent inside the request.** `Appointments.cs` calls `AppointmentNotifications`
(`OnScheduledAsync` for create and reschedule, `OnConfirmedAsync`, `OnCancelledAsync`, `OnLeftActiveAsync`) in the same
`SaveChanges` as the appointment change. These calls write rows, never mail: an
`AppointmentConfirmations` row per confirmation, and `Reminders` rows that work as the e-mail
outbox (`TemplateKey` `confirmation`, `reminder_1h` or `cancellation`). A mail failure can
therefore never fail the appointment change. A patient without `ContactEmail` gets no rows at
all.

**The worker sends.** `AppointmentNotificationWorker` is a `BackgroundService` that wakes every
60 seconds and calls `NotificationWorkerSteps`, one transaction per e-mail row, rows taken
with `FOR UPDATE SKIP LOCKED`:

1. Automatic cancellation: an open confirmation whose `DeadlineAt` has passed, on an
   appointment still `scheduled` and not yet started (`StartsAt` after now), cancels it (`CancelledBy = 'system'`, reason *Não
   confirmada no prazo*), queues the cancellation e-mail and adds a bell notice.
2. Sending: due `pending` outbox rows, 50 at a time. The worker creates the link's token at
   send time and stores only its SHA-256 (`AppointmentConfirmationTokens`), so no usable token
   exists in the database. A failure adds one to `Attempts` and retries on the next run with a
   new token; the third failure makes the row `failed`. A reminder for an appointment that is
   no longer `scheduled` or `confirmed` becomes `cancelled`, and one for an already confirmed
   appointment goes without a link.

The automatic-cancellation deadline starts only when the confirmation e-mail is actually
sent, in the same transaction as the send. A patient who never received the e-mail is never
cancelled for not confirming. If no e-mail sender is configured the rows simply stay
`pending` and the worker logs that sending is disabled.

Time comes from `TimeProvider` (registered as `TimeProvider.System`), so tests move a fake
clock and call the steps directly instead of waiting a minute.

**Configuration.**

- `Notifications:WorkerEnabled` (default `true`) turns the worker off. The test fixture sets
  it to `false` (`SharedAppHostFixture`), and `AppHost.cs` forwards it to the server as
  `Notifications__WorkerEnabled`.
- `PatientPortal:BaseUrl` is the base of the links in the e-mails
  (`{PatientPortal:BaseUrl}/confirmar/{token}`; the base already ends in `/patient`, and it is the same setting the invitation links use). The
  server refuses to start when an e-mail sender is configured (`Email:Provider`) and this is
  empty (`Program.cs`).

**The rule.** *Configurações* has a *Confirmação de consultas* section
(`components/settings/BookingPolicySection.tsx`), backed by
`GET`/`PUT /api/phi/providers/me/booking-policy`. The provider picks off, *N horas após o
e-mail* or *N horas antes da sessão*, with N from 1 to 168. A provider with no saved row
uses the default, 1 hour after the e-mail. Only these two fields (`BookingPolicies.AutoCancelMode`
and `AutoCancelHours`) are exposed. `ConfirmationRules.DeadlineAt` turns the rule into a
moment; if that moment is already past when the e-mail is sent, or falls at or after the session start
(for example *24 horas antes* on a session 3 hours away, or *5 horas após* on a session 3 hours away), there is no automatic
cancellation and the appointment stays `scheduled` for the provider to decide. The reminder
goes 1 hour before the session, and only when the appointment was created more than 1 hour
ahead.

**Hints on the calendar.** The details panel shows *Aguardando confirmação até {hora}* when
the appointment has an open deadline (`confirmationDeadlineAt` in the list response), and
*Paciente sem e-mail: não receberá confirmação.* when `patientHasEmail` is false. The
provider's own *Confirmar* button has the same effect as the patient confirming, except that
it creates no bell notice, because the provider did it.

**The bell.** `NotificationBell` (`packages/shared/src/ui/`) sits in `TopBar` and replaces the
icon that used to do nothing. It shows a badge with `unreadCount`, lists the notices
(*{paciente} confirmou a consulta de {data}* and *Consulta de {paciente} em {data} cancelada
por falta de confirmação*, each linking to that calendar day), offers *Marcar todas como
lidas*, and refreshes every 60 seconds while the app is open. It is backed by
`GET /api/phi/providers/me/notifications?page=1` (20 per page, newest first) and
`POST .../{id}/read` and `.../read-all`, all scoped to the signed-in provider. The notices are
`Notifications` rows with channel `in_app`; there is no e-mail to the provider.

E-mails are in Portuguese and carry the minimum: the provider's name, date, time (in the
appointment's time zone), modality and, when there is one, the link. Nothing clinical leaves
the server.

## What's explicitly a placeholder

`notes/$noteId.tsx` — the actual note editor, where reviewing and signing a note would
happen — is, in its entirety:

```tsx
function NoteEditorPage() {
  const { noteId } = Route.useParams();
  return (
    <div className="p-8 text-center text-onSurfaceVariant text-body-lg">
      Note {noteId} — coming soon.
    </div>
  );
}
```

`patients/$patientId/live-transcribe.tsx` — the screen where a provider would record and
watch a session get transcribed live — is the same shape, same "coming soon" text. These
aren't bugs or oversights to fix; they're honest placeholders for functionality that
depends on backend pieces (Chapter 5's PHI endpoints, and the SignalR hubs mentioned next)
that don't exist yet.

## The audio and real-time pieces: built, but not yet connected to anything

This is the single most important nuance in this chapter, and it's worth stating precisely
because it's easy to get wrong in either direction.

`useAudioRecorder.ts` (in `src/hooks/`) is a complete, working React hook wrapping the
browser's `MediaRecorder` API — permission handling, MIME-type negotiation (preferring
`audio/webm;codecs=opus`, falling back to `audio/mp4` for Safari), a running duration
timer, and a clean start/stop/reset lifecycle returning an in-memory `Blob`. Its own
comment notes it's "the direct web equivalent of the source app's expo-av-based hook" —
concrete evidence that at least parts of this frontend were ported from an earlier React
Native/Expo mobile app, consistent with the mobile client mentioned as a future
possibility in Chapter 6's design documents.

`lib/signalr.ts` is equally complete: a `transcriptionHub` (connect/disconnect, chunked
base64 audio streaming kept under SignalR's default 32KB message limit, a `finalize` call,
and `onFinalTranscript`/`onTranscriptionError` event subscriptions) and a `progressHub`
(subscribe to a job, `onProgress`/`onComplete`/`onError`) — client-side implementations of
exactly the pipeline-progress model described by `PipelineProgress` in Chapter 12's tour of
`types.ts`.

Here's the part worth being explicit about: **as of this writing, neither of these files is
imported anywhere except by their own definitions.** No component in `apps/provider` calls
`useAudioRecorder`, and nothing constructs a connection through `transcriptionHub` or
`progressHub`. This lines up exactly with what Chapter 5 already told you about the server
side — there are no SignalR Hub classes anywhere in `anamnys-aspire.Server` yet, so there's
nothing at `/hubs/transcription` or `/hubs/progress` for this client code to actually talk
to. This is client code written *ahead of* its backend contract: a complete, ready
implementation of the browser-side half of live transcription, waiting for both a UI that
calls it (presumably `live-transcribe.tsx`, once it's built out) and a server that can
receive what it sends. If you're asked to build either of those, this existing code is
almost certainly the starting point rather than something to write from scratch — just
don't assume it's already wired into a working feature because it exists and compiles.

## Settings

The account menu (`packages/shared/src/ui/AccountMenu.tsx`) leads to two pages under
`/account`. *Dados pessoais* edits the profile fields the application database owns
(`/api/phi/providers/me/profile`). *Acesso e segurança* does not implement password or
two-factor forms at all — a direct consequence of Chapter 6's identity design: each
button starts a Keycloak application-initiated action through the BFF's
`/auth/{realm}/action/{action}` route, rendered by the Anamnys login theme. If you're
ever asked to add a password or 2FA form in an SPA, the answer is "start the Keycloak
action," not "build a form here."

## The pattern to take away

`apps/provider` is a genuinely good illustration of how to read this whole repository:
some screens are complete, real, and worth using as a template; some are honest one-line
placeholders; and some supporting code (the audio/SignalR pieces here) is fully built but
sitting unconnected, waiting on a counterpart that doesn't exist yet on the other side.
Treat "this file exists and looks complete" and "this feature works end to end" as two
separate questions — this chapter is the clearest example in the codebase of why that
distinction matters.

Chapter 14 covers the other three SPAs, where the gap between "scaffolded" and "built" is
even starker.
