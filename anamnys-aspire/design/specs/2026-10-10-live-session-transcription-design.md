# Live Session — Transcription

**Date:** 2026-10-10
**Status:** Implemented
**Scope:** First delivery of "Sessão ao Vivo": the provider dictates in the browser, the audio
streams to the server over SignalR, Whisper.net transcribes it in-process when the recording
stops, and the result becomes a `Notes` row (`VoiceLive`) with the raw transcript, attached to
a `Sessions` row. Notion "Diagramas de Sequencia", flow 02.

"Sessão ao Vivo" is split into three deliveries, each with its own spec and plan:

1. **Transcription** (this document): Whisper.net, `TranscriptionHub`, the `/live-session`
   screen, a note with its raw transcript.
2. **Structuring:** LLamaSharp in-process turns the transcript into DAP / SOAP /
   Biopsychosocial sections (`NoteSections`), as one more step of the same worker.
3. **Review and signing:** the note editor (replaces the `notes/$noteId` placeholder), edits
   kept apart from the AI draft, sign-and-lock, `AuditEntries`.

The contracts here (note status, hubs, worker) are designed so 2 and 3 plug in without rework.

---

## 1. Context

- The provider app's side nav already has a "Sessão ao Vivo" item pointing to `/live-session`
  (`packages/shared/src/ui/Sidenav.tsx`, feature flag `liveSession`), but no route exists.
- `apps/provider/src/lib/signalr.ts` holds a `transcriptionHub` / `progressHub` client carried
  over from the deleted frontend. The server has no hub, no `AddSignalR`, no Whisper.net and no
  LLamaSharp. The provider app's Vite config already proxies `/hubs` with `ws: true`.
- i18n keys `notes.liveTranscribe.*` exist in `pt.json` (record, stop and generate, language,
  permission errors, connection states).
- Schema: `Notes.InputMode` already accepts `VoiceLive`; `Notes.Status` accepts `Draft`,
  `Processing`, `ReadyForReview`, `Signed`, `Exported` — no failure state. `Sessions`,
  `Transcripts` and `Appointments.SessionId` exist in SQL; only `Notes` has an EF entity, and it
  maps a subset of columns.
- Nothing creates a `Sessions` row yet (gaps file, "Notas clínicas").
- Flow 04 (capturing the patient's audio) is blocked by decision D-03, and the own video room
  (flow 17) is phase 3, pending D-06. Neither is in scope.

## 2. Decisions

| Topic | Decision |
|---|---|
| What is transcribed | What the provider dictates. The screen states that if the patient is audible, consent is required (flow 02's limiting rule). Capturing the session itself (flow 04) is out of scope. |
| When text appears | Only after the recording stops. No partial transcripts while speaking. |
| Transport | SignalR streaming in numbered chunks during the recording; the browser keeps every chunk in memory until the server confirms the finalize, and resends from the last confirmed chunk after a reconnect. |
| Audio format | The browser captures PCM 16 kHz mono 16-bit (AudioWorklet resampling), which is what Whisper.net consumes. No ffmpeg, no decoding on the server. |
| Where audio lives | Server memory only, keyed by recording id. Never disk, Redis, a durable queue, a log or a backup. Destroyed after transcription, on failure, on timeout and on restart. |
| Hardware | CPU only (`Whisper.net.Runtime`). Model size is configuration: `base` in development, `medium` or `large-v3-turbo` in production. |
| Model file | Downloaded on first use from Hugging Face into a configured directory on a persistent volume, checked against a SHA-256 pinned in configuration. Only the public model is downloaded; nothing leaves the server. |
| Processing | Asynchronous. Finalize creates the note as `Processing` and queues the job; one in-process worker transcribes one job at a time. |
| Session | Every recording creates or reuses a `Sessions` row. Picking one of the patient's appointments is optional; when picked, its session is reused or created and linked. |
| Where the transcript goes | `Notes.RawTranscript`. `Transcripts` stays reserved for session capture (flow 04). |
| Failure | New `Failed` note status with a short reason code. |
| Scale | One container, so no SignalR backplane. |

## 3. Data

Migration `design/migrations/2026-10-10-live-session.sql` (idempotent, one transaction, same
header and `psql` command as the earlier migrations), with the same change applied to
`anamnys-db-script.sql`:

- `Notes_Status_ck` accepts `'Failed'` as well.
- `Notes."FailureReason" text`, null unless `Failed`. Values: `no_speech`,
  `transcription_error`, `server_restart`. Never patient text. Check constraint:
  `("Status" = 'Failed') = ("FailureReason" IS NOT NULL)`.
- `Notes."RecordingId" uuid`, unique, null for other input modes. Makes finalize idempotent in
  the database, not only in memory.

EF:

- `Note` gains `SessionId`, `InputMode`, `RawTranscript`, `RecordingId`, `FailureReason`,
  `UpdatedAt`.
- New `Session` entity over the existing `Sessions` columns.
- `Appointment` gains `SessionId`.

Every query filters by the provider id resolved from the claim. The composite FK
`Sessions(PatientId, ProviderId) → Patients(Id, ProviderId)` already stops a session from
mixing providers.

## 4. Server

New folder `anamnys-aspire.Server/LiveSession/`, following `Appointments/` and `Notifications/`.

### 4.1 `TranscriptionHub` — `/hubs/transcription`

Accepts only the providers realm's cookie scheme; any other realm gets 401, as on `/api/phi`.

| Method | Behaviour |
|---|---|
| `BeginRecording(recordingId, patientId, appointmentId?, format, language)` | Checks that the patient, and the appointment when given, belong to the caller. Opens (or, if already open for this caller, keeps) the buffer. Records the start time. |
| `SendAudioChunk(recordingId, seq, chunk)` | `chunk` is base64 PCM (JSON hub protocol). Appends when `seq` is the next expected one, ignores a `seq` already received, rejects a gap. Returns the last accepted `seq`. Returns `buffer_expired` if the buffer is gone. |
| `FinalizeStream(recordingId)` | Writes the session and note (§5), hands the audio to the queue and returns `noteId`. Calling it again for the same recording returns the same `noteId`. With no note and no buffer, returns `buffer_expired`, as `SendAudioChunk` does. |

Limits: 90 minutes of audio per recording (the client warns at 85 and finalizes at 90); a
buffer with no chunk for 10 minutes is discarded. Every buffer belongs to the provider who
opened it; another provider's call on the same `recordingId` is refused.

### 4.2 `RecordingBuffers`

Singleton, in-memory dictionary keyed by recording id. Holds owner, patient, appointment,
format, language, start time, last `seq`, and the PCM bytes. Clearing a buffer zeroes the array
before releasing it. A timer sweeps expired buffers.

### 4.3 `TranscriptionQueue` and `TranscriptionWorker`

`Channel<TranscriptionJob>` plus a `BackgroundService` that processes one job at a time. For each
job: transcribe through `ITranscriber`, write `RawTranscript` and `ReadyForReview` (or `Failed`
with `no_speech` when the text is empty), destroy the audio in `finally`, and notify the
`ProgressHub`. Delivery 2 adds the structuring step between transcription and
`ReadyForReview`.

### 4.4 `ITranscriber` / `WhisperTranscriber` / `WhisperModelProvider`

`ITranscriber` takes PCM samples and a language and returns text, so the worker is testable with
a fake. `WhisperModelProvider` resolves the model on first use: downloads to a temporary file in
`LiveSession:ModelDirectory`, verifies the SHA-256, renames atomically, and loads a singleton
`WhisperFactory`. Configuration: `LiveSession:Model`, `LiveSession:ModelSha256`,
`LiveSession:ModelDirectory`. A failed download or hash mismatch fails that job
(`transcription_error`); the next job tries again.

### 4.5 `ProgressHub` — `/hubs/progress`

`SubscribeToNote(noteId)` checks the note belongs to the caller. Events: `NoteProgress(noteId,
stage, queuePosition)`, `NoteReady(noteId)`, `NoteFailed(noteId, reason)`.

### 4.6 Startup

A hosted step marks every `Processing` note with a `RecordingId` as `Failed`
(`server_restart`): its audio died with the previous process.

### 4.7 Packages and wiring

`Whisper.net` and `Whisper.net.Runtime`. `AddSignalR`, the two hubs mapped under `/hubs`, the
worker and the buffer sweep registered in `Program.cs`. In development the server is a host
process, so `LiveSession:ModelDirectory` defaults to a folder under the user's local application
data and survives restarts on its own; in production it must point at a persistent volume.

### 4.8 Reading a note

`GET /api/phi/providers/me/notes/{noteId}` returns the note's id, patient id and name, status,
input mode, format, raw transcript, failure reason and creation time; 404 for a note of another
provider. Documented in `anamnys-aspire.Server.http`.

## 5. Writing the session and the note

`FinalizeStream`, in one transaction:

1. With an appointment: reuse `Appointments.SessionId` if set; otherwise create a `Sessions`
   row with `ScheduledStart`, `ScheduledMinutes` and `Modality` from the appointment and link it.
2. Without an appointment: create a `Sessions` row with no schedule.
3. Either way: `ActualStart` = recording start, `ActualEnd` = finalize time,
   `Attendance = 'attended'`.
4. Create the `Notes` row: `Processing`, `VoiceLive`, the chosen format, `SessionId`,
   `RecordingId`.

If a note with this `RecordingId` already exists, return its id and write nothing.

## 6. Provider app

- **Route `/_app/live-session`**, three stages on one screen:
  1. **Prepare:** patient (existing patient search), one of the patient's appointments for today
     (optional), format (stored now, used by delivery 2), language (Portuguese by default), and
     the consent notice.
  2. **Recording:** timer, level meter, pause/resume, "Parar e gerar nota", connection indicator.
  3. **Processing:** stage and queue position from the `ProgressHub`; on `NoteReady` navigate to
     `/notes/$noteId`; on `NoteFailed` show the reason and offer to record again.
- **`useLiveRecorder` hook:** `getUserMedia`, an AudioWorklet resampling to 16 kHz mono Int16,
  ~16 KB chunks numbered by `seq`, kept in memory, sent in order. On reconnect it resends from
  the last accepted `seq`; on `buffer_expired` it calls `BeginRecording` again and resends
  everything. Releases the audio once `FinalizeStream` returns.
- **`signalr.ts`:** rewritten to the contract in §4.1 and §4.5, keeping the manual base64
  encoding.
- **`notes/$noteId`:** in this delivery it shows only the state (processing / failed / ready)
  and the raw transcript, read-only.
- **Leaving the page:** `beforeunload` and a TanStack Router navigation block while recording or
  while audio is unconfirmed.
- **i18n:** reuse `notes.liveTranscribe.*`; add the missing keys to `pt.json`.

## 7. Failure handling

| Situation | Behaviour |
|---|---|
| Microphone denied or missing | Existing messages; recording does not start. |
| Connection drops while recording | Capture continues in the browser; "Conectando…"; resend from the last accepted `seq`. |
| Server buffer expired during a long drop | `buffer_expired`; the client reopens and resends everything it holds. |
| Tab closed or browser crashes | Audio lost, by design (never persisted). `beforeunload` warns first. |
| 85 / 90 minutes reached | Warning, then automatic finalize. |
| Model download fails or hash mismatch | Note `Failed` (`transcription_error`); log without PHI; next job retries the download. |
| Empty transcript | Note `Failed` (`no_speech`); the screen offers to record again. |
| Whisper throws | Note `Failed` (`transcription_error`). |
| Server restarts | Buffers and queue are gone; orphan `Processing` notes become `Failed` (`server_restart`). |

In every path the audio is destroyed in `finally`. Logs never carry transcript text or audio.

## 8. Testing

Run with `dotnet run --project anamnys-aspire.Tests`.

- Hub: a patients- or owners-realm credential gets 401; a patient or appointment of another
  provider is refused; another provider cannot write to a recording; a repeated
  `FinalizeStream` returns the same `noteId`; a duplicated `seq` is ignored; a gap is rejected.
- Session and note writing: new appointment, appointment that already has a session, no
  appointment.
- Worker with a fake `ITranscriber`: success, empty text, exception; the buffer is cleared in
  all three.
- Startup: an orphan `Processing` note becomes `Failed`.
- Real Whisper on a short WAV fixture, run only when the model file is present.
- Note endpoint: 401 without a session, 404 for another provider's note, 200 with the transcript.
- Frontend: the provider app has no test runner, so resampling and resend are verified manually.
- Manual: the full flow in the running app, including cutting the network mid-recording.

## 9. Out of scope

- LLaMA structuring (delivery 2), editing and signing (delivery 3).
- Capturing the patient's audio and the consent state machine (flow 04, D-03).
- Video room (flow 17, D-06).
- Partial transcripts while speaking.
- GPU runtime.
- `AccessLogs` and `AuditEntries` writes (existing gap; audit comes with delivery 3).
