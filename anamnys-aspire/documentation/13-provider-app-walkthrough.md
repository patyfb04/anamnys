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

Forms read field errors from `ApiError.errors` (`packages/shared/src/api/client.ts`), the
server's `ValidationProblem` body, and show them next to each field.

`patients/$patientId/notes.tsx` is a thin route wrapper around `PatientNotesView`, which
follows the same shape as the record page: a `useQuery` call, explicit loading and error
states, shared UI kit components (Chapter 12) and `useTranslation` for every piece of
user-facing text. `NoteCard` backs its list, and `AccountMenu` backs the account menu in
the app shell. The notes it lists come from `notesApi`, whose server routes do not exist
yet.

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
