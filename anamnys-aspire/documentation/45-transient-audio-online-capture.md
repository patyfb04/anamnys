# Chapter 45: Transient Audio and Online Session Capture (F-25, F-26)

How the audio of a recording lives only in memory and is destroyed on every ending, how each ending leaves one
destruction record, how the tests prove that no audio artefact survives, and how an online session captures the call's
audio in the browser (browser tab, screen with system audio, or the loudspeaker). Last checked against the code on
2026-11-16 (branch `feature/transient-audio`, not yet merged into `develop`, nothing committed). Design:
`design/specs/2026-11-16-transient-audio-tab-capture-design.md`. Depends on chapter 44 (recording consent), whose capture
rule is unchanged.

What a future change has to respect: audio exists only in memory (server buffers and queue jobs, the browser's chunk
log); no durable queue, file, bucket object, database column, log line or backup may ever hold it (section 2); the audio
is **zeroed before anything else** happens on every ending, and then one `AudioDestructionLogs` row is written for each
server copy of the audio that is destroyed (section 4; a recording re-sent after its buffer was swept ends with more than
one row); the proof tests (section 5) must keep covering every ending, including a failed finalize;
audit values carry ids and codes only (section 10); the migration is applied before the server that reads it (section
3); the pipeline never records "silently microphone-only" when the call's audio was expected (section 8); the browser
source mixer takes pluggable sources so Group H2's own-room track needs no change elsewhere (section 7); and the speaker
method keeps echo cancellation **off** while the microphone of a direct capture keeps it **on** (section 7).

---

## 1. Purpose and scope

F-26 (transient audio pipeline: the destruction is recorded and proven) and F-25 (capture of the call's audio for online
sessions), the second delivery of group G. Done when (backlog): F-26, an integration test proves that no audio artefact
survives anywhere after a completed session, including the failure path; F-25, the audio of an external meeting reaches
our transcription pipeline.

Decisions agreed with the user (2026-11-15):

| Topic | Decision |
|---|---|
| What an online session records | The microphone (the provider) and the call's audio (the patient) mixed into one stream. |
| Choosing in-person / online | Sessão ao vivo has "Presencial / Online", preselected from the linked appointment's `Modality` (otherwise "Presencial"); dictation is always microphone only. |
| Source of the call's audio | The browser tab of the call (Meet, Zoom web, Teams web) or, in Chrome/Edge on Windows, the entire screen with "share system audio" (covers the installed Zoom/Teams apps; every sound of the computer is recorded). |
| Audio must always be recordable | Besides direct capture, "Pelo viva-voz": the call plays on the loudspeaker and the microphone records both voices, with echo cancellation OFF. Available everywhere; the only online method on mobile and on desktop Firefox/Safari. |
| Share without audio | The recording does not start; the page explains how to tick "Compartilhar áudio". |
| Sharing stops mid-session | The recording pauses at once; "Compartilhar de novo", "Trocar método" or "Encerrar gravação". Never silently microphone-only. |
| Prepared for Group H2 | The mixer takes pluggable sources; the method can be switched mid-recording within the same recording (the base of F-43). The record keeps every source used. |
| Destruction record | Every server copy of a recording's audio (session and dictation) that is destroyed gets one `AudioDestructionLogs` row: `transcribed`, `failed` or `abandoned`. A recording re-sent after a sweep or a failed finalize has an earlier `abandoned`/`failed` row (note null) and then its final row. |

Out of scope (section 13): direct capture of call apps on mobile, system audio on macOS, recording with the screen off,
speaker separation, recording video or the screen, pseudonymisation (F-27), transcript as source (F-28).

---

## 2. The audio lifecycle and guarantees

```
browser (memory only)            server (memory only)
mic + call  --mixer-->  PCM16 chunks  -->  TranscriptionHub.SendAudioChunk
                                              |
                                      RecordingBuffers (keyed by recording id)
                                              |  FinalizeStream
                                      TranscriptionQueue job (in memory)
                                              |
                                  NoteTranscription.ProcessAsync
                                              |
                      transcript written, audio zeroed (Array.Clear + ZeroMemory)
                                              |
                              AudioDestructionLogs row + audio_destroyed audit
```

Guarantees (these are the text of the promise; the tests in section 5 hold the code to them):

- Audio goes to no disk, database column, object-store bucket, durable queue or log. There is nothing to reprocess.
- **The audio is zeroed before anything else** on every ending (completed, transcriber exception, no speech, a worker
  failure before transcription, an abandoned buffer swept for idleness, a failed finalize); only then are the record and
  the audit written. A failure to write the record is logged (recording id and exception type only) and never keeps
  audio alive.
- Audio sent **after** the finalize (for example a begin retried after it) is destroyed too. When the client calls the
  finalize again, the finalize takes the reopened buffer for that recording id, destroys it and adds no second row.
  Otherwise (the page was left) the sweeper destroys it once idle and writes an `abandoned` row.
- **The rule is one row per server copy of the audio that is destroyed, not one per recording.** A recording whose buffer
  was swept during a long pause, suspension or disconnection (more than `BufferIdleTimeout`, 10 minutes), or whose
  finalize failed, is re-opened by the client under the same recording id and every chunk is re-sent. The first copy was
  zeroed and recorded (`abandoned` or `failed`, note null); the second ends in its own row (`transcribed`, normally). No
  audio survives in any case, and the note page shows the latest row of the note.
- A resume (`BeginRecording` with an existing recording id) must keep the mode of the buffer: another mode is refused
  with `invalid`, so the consent gate that opened the buffer cannot be bypassed on a reconnect. The expired-buffer half
  of that hole (a new buffer under a remembered id) cannot be closed without remembering recording ids and is listed in
  section 13.
- **A server restart loses the in-memory audio, with no destruction row and nothing to recover.** That is by design: no
  audio exists anywhere to destroy.
- **Backups (F-46) hold no audio.** They copy the database and the object store, and neither holds audio.
- The browser keeps audio only in memory (the `ChunkLog`); no IndexedDB, no localStorage. On stop, finalize or
  unmount, every track is stopped and the nodes disconnected.

---

## 3. Data and migration

Migration `design/migrations/2026-11-16-transient-audio.sql` (idempotent, mirrored at the end of `anamnys-db-script.sql`
without BEGIN/COMMIT, appended to the migration-test chains after `2026-11-15-recording-consent.sql`).
**It must be applied to any persistent environment before the new server version starts.** Without it, `GET` note
(it selects the new columns), the discard and the destruction writes fail (`42703: column ... does not exist`); the
destruction write failures are logged and do not break the transcription, but the note read does. Until the dev
database has it, dev-database suites such as `NoteEndpointTests` fail; stop Aspire before running the suite.

`AudioDestructionLogs` (schema only before, never written by code):

- `SessionId` now holds the **recording id** (its foreign key to the sessions table was dropped).
- Added: `ProviderId` uuid NOT NULL (FK `Providers`, cascade), `PatientId` uuid NOT NULL (composite FK
  (`PatientId`, `ProviderId`) to `Patients(Id, ProviderId)` as `AudioDestructionLogs_Patient_fk`, cascade), `NoteId` uuid
  null (FK `Notes`, on delete set null), `RecordingMode` (`session|dictation`), `Sources` text[].
- Checks: `Outcome` is `transcribed|failed|abandoned`; `Sources` has at least one entry, every entry is one of
  `in_person|online_capture|online_speaker` (array containment) and a dictation has exactly `{in_person}`.
- Indexes: `AudioDestructionLogs_NoteId_idx` on (`NoteId`, `DestroyedAt` DESC) where the note is set, and
  `AudioDestructionLogs_Provider_Patient_idx`.
- Old rows (none are written by code) are filled from their session (mode `session`, sources `{in_person}`); a row that
  cannot be filled is deleted.
- The trigger `AudioDestructionLogs_patient_not_discarded` refuses new rows for a discarded record: an `abandoned` row
  for a record discarded mid-recording fails and is logged.
- Audit actions added to the list guarded by the `AuditEntries` check: `audio_destroyed` and
  `recording_source_switched` (the migration's guard looks for `'%recording_source_switched%'`).

`RecordingMeta` (memory) gains `Mode` (`session|dictation`) and `Sources` (ordered set, first use first).

---

## 4. The destruction record

`AudioDestruction.RecordAsync(db, recordingId, meta, noteId, outcome, now, ct)` writes the row and the audit entry; it
throws on failure but detaches its two entities first so a later save does not retry them. `TryRecordAsync(..., logger,
ct)` wraps it: it never throws (except cancellation at shutdown) and logs only the recording id and the exception type
(`Audio destruction record failed for recording {id}: {ExceptionType}`).

One row per destroyed server copy of the audio, on **every** ending (a recording whose buffer was swept or whose finalize
failed, and which the client then re-sends under the same id, has more than one row, for example `abandoned` then
`transcribed`):

| Ending | Outcome | NoteId |
|---|---|---|
| Transcript written | `transcribed` | the note |
| Transcriber exception, or no speech (the row is written first, then the note is marked failed, so the note page never shows a failed note without the line) | `failed` | the note |
| The job fails before `ProcessAsync` returns (for example the first progress notification throws) | `failed` | the note |
| `LiveRecordings.FinalizeAsync` transaction fails (the audio is destroyed, the change tracker cleared, the row written with the hub's logger, the exception rethrown) | `failed` | null |
| Buffer idle past the limit, swept by `RecordingBufferSweeper` (memory zeroed under the lock, rows written outside it through a scoped `DbContext`) | `abandoned` | null |

The row carries provider, patient, mode, the ordered sources used, the time and the outcome. It is not tied to the
note's own trail (the audit entry has `NoteId` null).

Note view (`GET` note): `audioDestroyedAt`, `audioOutcome`, `audioSources` from the latest row for the note, scoped by
provider (the earlier `abandoned`/`failed` rows of a re-sent recording have no note, so the note shows its own row); all
null when the note did not come from a recording or predates this change.

---

## 5. The F-26 proof

`anamnys-aspire.Tests/LiveSession/AudioDestructionTests.cs`, on the consent scene's scratch database. The tests drive the
same calls the hub makes (`CheckBeginAsync`, `NewMeta`, `Begin`/`Append`, `FinalizeAsync`) and then
`NoteTranscription.ProcessAsync`, the worker or the sweeper. Endings covered: completed, transcriber failure, swept
(abandoned), failed finalize, worker failure before transcription, a begin retried after the finalize, and a recording
swept and then re-opened with the same id (abandoned, then transcribed). Every ending
asserts:

- **Buffer:** the entry is gone and its backing bytes are all zero (internal test hook `BackingBytes`). The completed
  test sends 72,000 bytes in 45 chunks so the buffer grows past 64 KiB; the replaced array must be zeroed at once and
  still be zero at the end. The decoded samples the transcriber received are non-empty and are zero after the job.
- **Database:** the full list of `bytea` columns equals a **whitelist** of non-audio columns (token hashes and anchors:
  `AppointmentConfirmationTokens.TokenHash`, `Dossiers.AnchorToken`, `IntegrityAnchors.Token`,
  `PatientInvitations.TokenHash`, `RecordingConsents.SignTokenHash`, `RecordingConsents.RevokeTokenHash`). A new `bytea`
  column anywhere fails the test until it is reviewed and listed.
- **Object store:** the real `notes` bucket is listed before and after (key to ETag). Every object that is new or whose
  ETag changed is read (up to 64 MiB) and must not contain the recording's random 32-byte **marker**, which the test
  audio is built from; no key contains the recording, note, patient or provider id. Other test classes write to the
  bucket in parallel, which is why the check is by marker and not by object count.
- **Temp directory:** the process temp directory is listed before and after (recursion depth 2); every file that appeared
  or changed is read and must not contain the marker.
- **Logs:** a capturing logger shows no line containing text standing for echoed input; the only line of a transcriber
  failure is exactly `Transcription failed for note {noteId}: InvalidOperationException`.
- **Record:** one `AudioDestructionLogs` row per destroyed server copy, with the right outcome, mode, sources, provider,
  patient, note and time, and one `audio_destroyed` audit entry per row with the expected value. A begin retried after
  the finalize, followed by a second finalize, adds no second row, and the sweep then removes 0 buffers. A recording
  swept, re-begun with the same id, re-sent, finalized and transcribed ends with exactly two rows (`abandoned` with note
  null, then `transcribed`), both buffers zeroed, and the note view showing the `transcribed` row.

Method note: the proof is by **marker scans of new or changed temp files and bucket objects**, a whitelist of `bytea`
columns, and a capturing logger; it does not look for the absence of every possible sink, so a new place that can hold
bytes (a new table type, a new bucket, a log sink) must be added to these checks when it appears.

---

## 6. Sources and switching

Sources (`RecordingSources`): `in_person` (the microphone in the room), `online_capture` (microphone plus the call's
audio from a tab or screen) and `online_speaker` (microphone only, call on the loudspeaker, echo cancellation off).

- `BeginRecording(recordingId, patientId, appointmentId, format, language, mode, dictationConfirmed, source)`: `source`
  is the initial one. An unknown source is `invalid`; dictation with an online source is `invalid` (checked before the
  dictation audit row). A resume (same recording id and mode) **adds its source to the set when it is new**, without a
  switch audit.
- `SwitchSource(recordingId, source) : SwitchAck`, statuses `ok|invalid|expired|forbidden`: `invalid` for an unknown
  source or a dictation; `expired` when there is no buffer for the id; `forbidden` for another provider's recording. On
  `ok` it adds the source to the ordered set (the meta is replaced, never mutated, so a meta handed out earlier does not
  change) and writes the audit `recording_source_switched`, on every `ok`, including a switch back to a used source.
  The chunks keep flowing to the same buffer and the sequence numbering does not change, so the audio before and after a
  switch ends in **one** transcription.
- **The browser calls `SwitchSource` only after the new media joined the mixer**, so the server's set, the audit and the
  destruction row never list a method that recorded nothing. A reconnect or a `buffer_expired` re-open calls
  `BeginRecording` once per source used (in order of first use), so a buffer swept during a disconnection is re-created
  with every source and the destruction row still lists all of them.
- The destruction row keeps every source used in order of first use, for example `{online_capture, online_speaker}`.

---

## 7. Browser capture

Code: `apps/provider/src/lib/audioCapture.ts` (pure functions, the state machine, sources, the mixer and the wake lock)
and `apps/provider/src/hooks/useLiveRecorder.ts` (the hook that wires them to the worklet and the hub).

**Methods**

| Method | How | Echo cancellation / noise suppression |
|---|---|---|
| `in_person` | `getUserMedia` microphone | default |
| `online_capture` | `getDisplayMedia({ video: true, audio: { echoCancellation: false, noiseSuppression: false }, systemAudio: "include", selfBrowserSurface: "exclude", preferCurrentTab: false })` for the call, plus the microphone | microphone **on** (the patient's voice comes from the call's stream, so echo of the call picked up by the microphone is better cancelled); call stream off |
| `online_speaker` | microphone only, `{ channelCount: 1, echoCancellation: false, noiseSuppression: false, autoGainControl: true }` | **off** (it would erase the patient's voice from the loudspeaker) |

- The video track of a share is stopped and removed at once; if the returned stream has no audio track, every track is
  stopped and the error is `share_without_audio` (the recording does not start). Cancelling the picker is
  `share_cancelled`; an unsupported browser is `share_unsupported`.
- The share picker is the first await after the click, to keep the user's activation.
- After a microphone is opened, `getSettings()` is compared with the requested echo cancellation and noise suppression
  (`processingMatches`); on a mismatch the track is stopped and asked again with exact constraints (`exactProcessing`);
  if the device refuses, the plain stream is kept (mismatch remains, a gap in section 13).

**Support matrix** (`captureSupport(env)`, from `userAgentData` brands, mobile flag and platform, falling back to the
UA string and iPadOS touch detection)

| Environment | Tab capture | System audio (screen) | Online methods offered |
|---|---|---|---|
| Chrome / Edge desktop, Windows | yes | yes | capture (default) and speaker |
| Chrome / Edge desktop, macOS or Linux | yes | no | capture and speaker |
| Firefox, Safari (desktop) | no | no | speaker only, with the explanation |
| Any mobile browser (Android Chrome, iOS Safari, ...) | no | no | speaker only, with the explanation |

In person and dictation work everywhere. Where direct capture is unsupported, "Online" shows only the speaker method and
"Neste navegador o áudio da chamada é captado pelo alto-falante. Deixe a chamada no viva-voz, sem fone."

**Mixer and pluggable sources.** An `AudioSource` has `id`, `kind` (`microphone|call`), `start(ctx)`, `stop()`,
`onEnded(cb)`, `level()` and `settings()`. The factories acquire the media when called: `microphoneSource(constraints)`
and `displayAudioSource()`. `AudioMixer(ctx, destination)` builds a mono bus (a `GainNode` with `channelCount` 1) into
the existing `pcm16-recorder` worklet; `add(source)` replaces a source with the same id (used to swap the microphone
constraints), connects it through its own `GainNode`; `remove(id)` disconnects and stops the tracks; `levels()` gives one
level per kind (`AnalyserNode` per source); `close()` removes everything. A closed flag makes a late `add` stop its
source and throw, so no track outlives the chain. The `AudioContext` and the worklet are never restarted.
`onEnded` fires once when the track ends on its own, never after `stop()`, and fires at once if the track had already
ended.

**How Group H2's own-room remote track plugs in:** H2 adds one more factory, `remoteTrackSource(track)`, that wraps the
remote participant's `MediaStreamTrack` in a `streamSource` (kind `call`) and gives it its own id; the hook then calls
`mixer.add` for it exactly like the display source, and the source set sent to the server needs one more code (extend
`RecordingSources`, the migration's check and the type `RecordingSource`). The worklet, the chunking, the hub, the
buffers and the destruction record stay as they are. Switching from the own room to an external tool (F-43) is then a
method switch within the same recording (`SwitchSource`).

**Other pure helpers:** `sourceFor(mode, place, method)`, `microphoneConstraints(source)`, `switchTargets(source,
support)`, `placeFromModality(modality)`, `defaultMethod(support)`, `shareErrorCode(e)`, `microphoneErrorCode(e)`,
`pauseWhenHidden(env)`.

---

## 8. Pause, resume, wake lock and share loss

States: `recording | paused | suspended | share_lost` (the recorder wraps them with `idle | finalizing | done |
error`), with the pure `transition(state, event)`:

| Event | Effect |
|---|---|
| `share_ended` | to `share_lost` from any state (never silently microphone-only) |
| `pause` | `recording` to `paused` |
| `suspend` | `recording` to `suspended` |
| `resume` | `paused` or `suspended` to `recording` (a lost share is unchanged) |
| `share_restored` | `share_lost` to `recording` |
| `switched` | `paused` stays `paused`; anything else to `recording` |

- **Share lost** (the call's audio track `ended`): the call source is removed from the mixer, the state goes to
  `share_lost` and the worklet pauses at once. The panel offers "Compartilhar de novo" (picks the tab again, same
  recording, no server call), "Trocar método" and "Encerrar gravação". A lost share has no plain resume.
- **Suspension:** `suspend` is raised by the `AudioContext` leaving `running` (including Safari's `interrupted`), by the
  page becoming hidden **on mobile only** (`pauseWhenHidden(env)`), or by the OS ending the microphone track (`micLost`;
  "Retomar" reopens it with the current source's constraints, which may ask permission again). **On desktop a hidden page
  does not pause:** the provider sits in the Meet/Zoom tab for the whole call, so our tab is hidden all the time and
  Chromium keeps a capturing page running; only a real `AudioContext` suspension pauses there. (This differs from the
  spec's literal wording; it was ruled during the build and is to be confirmed by the user.) On return the notice "A
  gravação foi pausada pelo navegador. Retomar?" appears for every recording (in person, online, dictation); "Retomar"
  continues the same recording id (the existing reconnect/resume path; the server buffer keeps what was sent).
- **Wake lock** (`navigator.wakeLock.request("screen")`): requested at start, again on `visibilitychange` to visible
  while capturing and on resume, released on stop, failure and unmount. One pending request is shared by overlapping
  calls, and a sentinel obtained after `release()` is released.
- **Cue outside our tab:** while the state is `share_lost` or `suspended`, `document.title` gets the prefix
  "Gravação pausada · ". In `share_lost`, if the page is hidden **and `Notification.permission` is already `"granted"`**, a
  system notification "Gravação pausada" with "O compartilhamento da chamada parou. Volte ao Anamnys para compartilhar de
  novo ou encerrar." appears (no patient data); permission is **never requested** by the app, and the constructor is in a
  try/catch (Android Chrome only allows notifications through a service worker).
- **Switch in progress:** while a method switch runs the state still changes but the worklet receives no pause or resume
  until the switch ends (then it is re-synced); the hook exposes `busy`, which disables "Compartilhar de novo" and the
  switch buttons. `switchMethod` and `reshare` capture the audio chain on entry and, after every await, check that the
  recording is still the same (not stopped, same generation); if not, they stop the newly acquired media and return
  without calling `fail`.
- **Switch order** (`switchMethod`): picker (capture only), pause the worklet, resume the context if needed, remove the
  old microphone (stopping its track) and open the new one, remove the old call, mix the new media, and only then
  `SwitchSource`. If the new microphone fails, the previous method's microphone is reopened and "Não foi possível abrir
  o microfone." is shown (otherwise `suspended` with the microphone marked lost). If the RPC answers `invalid` after
  the media joined, the new media is removed and the recording falls back to `share_lost` (previous method was capture)
  or `suspended`. If only part of the new method joined, the local source becomes the target with the announcement
  pending: a missing call goes to `share_lost`, a missing microphone to `suspended`, and `resume`/`reshare` announce the
  method once their media is in.

---

## 9. UI

Provider (`apps/provider/src`):

- `components/live-session/OnlineMethodChoice.tsx`: "Presencial / Online" (session mode only; dictation is always
  `in_person`) and, under Online, "Captar o áudio da chamada" (where supported, default) or "Pelo viva-voz". The place is
  preselected from the selected appointment's `modality`. Before the browser picker it shows the instructions: the tab of
  the call with "Compartilhar áudio da aba", and on Windows the entire screen with "Compartilhar áudio do sistema" for
  the Zoom/Teams apps ("todo som do computador será gravado; feche outros sons").
- `components/live-session/CaptureStatusPanel.tsx`: the banner "Informe o paciente verbalmente que a sessão está sendo
  gravada." (`InformPatientBanner`, sticky at the top of the page column while an online recording is in a capture
  state); the two meters "Você" and "Chamada" (one meter for the speaker method, with the banner "Deixe a chamada no
  viva-voz, sem fone."); the current method and "Trocar método" ("Trocar para captar a chamada", "Trocar para o
  viva-voz"; the switch to capture only where supported); the share-lost panel; the suspension notice; source errors.
- `components/live-session/RecordingCard.tsx`: `suspended` and `share_lost` count as live but paused; "Retomar" for
  `paused` and `suspended`; "Parar e gerar nota" does the same as "Encerrar gravação". Leaving the page in any live state
  asks for confirmation.
- `routes/_app/live-session.tsx`: place and method state; `holdsAudio` includes the new states; start-time errors
  (`share_without_audio`, `share_cancelled`, `share_unsupported`) put the recorder in `error` and the recording does not
  start; errors during a recording (share again, switch) go to `sourceError` and leave the recording running or paused.
- `routes/_app/notes/$noteId.tsx`: under the header, "Áudio da sessão destruído em {data} às {hora}, após a
  transcrição." (or ", sem transcrição." for `failed` and `abandoned`) and a "Sessão online" badge when `audioSources`
  contains `online_capture` or `online_speaker`.
- `lib/signalr.ts`: `begin(...)` takes `source` as the 8th argument; `switchSource(recordingId, source)`.
- `packages/shared/src/lib/types.ts`: `NoteDetail` gains `audioDestroyedAt?`, `audioOutcome?`, `audioSources?`; types
  `AudioOutcome` and `RecordingSource`.

Copy in `packages/shared/src/lib/i18n/locales/pt.json` under `liveSession.online.*` (place, method, hints,
`unsupported`, `instructions*`, `speakerBanner`, `informBanner`, `meters`, `switchTo`, `shareLost`, `suspended`,
`titlePaused` = "Gravação pausada", `notifyShareLost`, `errors.*`) and `notes.audio.*` (`destroyed.transcribed|failed|
abandoned`, `online` = "Sessão online"). Error texts: `share_without_audio` explains how to tick "Compartilhar áudio da
aba" (or "Compartilhar áudio do sistema" on the entire screen); `switch_failed` is "Não foi possível trocar o método
nesta gravação."; `device` is "Não foi possível abrir o microfone."; `permission` is "É necessário acesso ao microfone.".

---

## 10. Audit

| Action | Value (ids and codes only) |
|---|---|
| `audio_destroyed` | `{recordingId} {noteId or -} {outcome}`; `FieldChanged` is `recording`; `NoteId` null; the entry stays after a discard (only ids) |
| `recording_source_switched` | `{recordingId} {source}`; `FieldChanged` is `recording`; written by `SwitchSource` on every `ok` |

There is no begin audit for a session (a deviation from spec section 2.2, which assumed one): the source is kept in the
meta and in the destruction row, and `recording_source_switched` is the only audit value that names a source. A
dictation writes one `dictation_confirmed` per begin (chapter 44), so a dictation reconnect writes one row more. No audio, transcript or free text enters an
audit value, a log line or an exception message that is logged.

---

## 11. Discard

`DiscardService` deletes the patient's `AudioDestructionLogs` rows by patient and provider **before** the notes (term
count key `audioDestructionLogs`). The rows have a `PatientId` column so discard and scoping are direct; the comment that
said they cascade was corrected. The `audio_destroyed` and `recording_source_switched` audit entries carry only ids and
stay, like `dictation_confirmed`.

---

## 12. Testing

Server (scratch databases, Aspire stopped): `LiveSession/AudioDestructionTests.cs` (the F-26 proof, section 5, plus the
note view fields for the owner, null for a typed note and nothing for another provider, `TryRecordAsync` leaving the
context clean on a failed write, and one transcription across a source switch with the row listing both sources),
`LiveSession/LiveRecordingModesTests.cs` (every known source accepted, unknown refused for session and dictation,
dictation refuses online sources without audit, `NewMeta`, resume adds a source in order, `SwitchSource` ok/invalid/
dictation/expired/forbidden, same buffer and sequence after a switch), `LiveSession/TranscriptionHubTests.cs` (8-argument
`BeginRecording`, `SwitchSource` over a real hub), `LiveRecordingsTests`, `TranscriptionWorkerTests`,
`NoteTranscriptionTests`, `RecordingBuffersTests` (the swept bytes are zeroed), `DiscardServiceTests` (the seed adds a
destruction row), `Data/TransientAudioMigrationTests.cs` and the migration chains (22 chains run the new file after the
consent migration; the retention-panel chain counts 20 triggers instead of 19). Results at the end of task 2: 84/84 in
the classes above. `NoteEndpointTests` need the migration on the dev database.

Provider app: **there is no unit-test runner**; `audioCapture.ts` keeps its logic in pure functions so tests can be added
once a runner exists (gap, section 13). `npm run build` (tsc -b and vite build) and `npm run lint` pass in
`apps/provider` after the final fix round. Browser behaviour is verified only by the manual script below.

**Manual check script** (provider app, a patient with a valid recording consent, session mode):

1. **Meet tab in Chrome (desktop).** Choose Online, then "Captar o áudio da chamada". Start, pick the Meet tab and tick
   "Compartilhar áudio da aba". Both meters move; the banner "Informe o paciente..." stays on screen. Go to the Meet tab
   for a minute and come back: the recording kept running and was not paused. Stop: the note page shows "Áudio da sessão
   destruído em ... após a transcrição" and the "Sessão online" badge, and the transcript has both voices. After stop the
   screen share ends and no video was captured. In `chrome://webrtc-internals` (or the track's `getSettings()`), the
   microphone of `online_capture` has echoCancellation and noiseSuppression `true`.
2. **Share without audio.** Start, pick a tab without ticking audio: the recording does not start and the message
   explains how to tick "Compartilhar áudio".
3. **Installed Zoom on Windows (Chrome or Edge).** The instructions mention the entire screen with "Compartilhar áudio do
   sistema". Pick "Tela inteira" and tick system audio: the "Chamada" meter moves with the Zoom audio; every computer
   sound is captured, as the warning says.
4. **Share stopped mid-recording** (click "Parar de compartilhar" in Chrome's bar): the recording pauses at once, the
   share-lost panel appears, the timer stops, the tab title starts with "Gravação pausada · ". (a) "Compartilhar de
   novo": pick the tab again; the same recording continues and there is one note with all of the audio. (b) Repeat, then
   "Trocar para o viva-voz": the recording continues with one meter and the speaker banner; the audit has
   `recording_source_switched`, the destruction row has `{online_capture, online_speaker}`; the microphone's
   `getSettings()` has echoCancellation and noiseSuppression `false`. (c) Repeat, then "Encerrar gravação": it finalizes
   into a note.
5. **Switch from the recording bar.** During a capture recording, "Trocar para o viva-voz", then "Trocar para captar a
   chamada" (the picker opens). Same recording, one note; check the microphone processing flags after each switch as in
   4b (speaker `false`, capture `true`).
6. **Phone browser, call on speaker** (Android Chrome and iOS Safari). Online shows only the "Neste navegador..."
   explanation. Start: one meter and "Deixe a chamada no viva-voz, sem fone." The patient's voice from the loudspeaker is
   in the transcript (echo cancellation off). The screen does not dim during the recording (wake lock).
7. **Screen off or app switch on the phone** for about 20 s: on return, "A gravação foi pausada pelo navegador.
   Retomar?" appears and the timer stopped. "Retomar" continues the same recording; stop gives one note with the audio
   from before and after. On iOS, if the OS ended the microphone, "Retomar" reopens it (it may ask permission again).
8. **Desktop Firefox.** Online offers only the speaker method, with the explanation; in person works.
9. **Dictation.** There is no Presencial/Online choice; it works as before; the note page shows the destruction line and
   no "Sessão online" badge.
10. **Leaving the page** during a recording, or in `share_lost` or `suspended`, asks for confirmation; after leaving, no
    tab is still shared and the microphone indicator is off.

---

## 13. Deployment, where things are, and gaps

Deployment: apply `design/migrations/2026-11-16-transient-audio.sql` to every persistent environment **before** the new
server starts; a server restart loses in-memory audio with no row (by design). The Confluence page is updated
separately.

| Piece | Path |
|---|---|
| Migration | `design/migrations/2026-11-16-transient-audio.sql` |
| Entity | `anamnys-aspire.Server/Data/Entities/AudioDestructionLog.cs` |
| Server code | `anamnys-aspire.Server/LiveSession/` (`AudioDestruction`, `RecordingBuffers` with `RecordingMeta` and `RecordingSources`, `RecordingBufferSweeper`, `LiveRecordings`, `TranscriptionHub`, `TranscriptionQueue`, `TranscriptionWorker`, `NoteTranscription`, `LiveSessionContracts` with `SwitchAck`) |
| Other hooks | `Notes/NoteQueries.cs`, `Retention/DiscardService.cs`, `Data/AnamnysDbContext.cs` |
| Provider UI | `apps/provider/src/lib/audioCapture.ts`, `hooks/useLiveRecorder.ts`, `lib/signalr.ts`, `components/live-session/` (`OnlineMethodChoice`, `CaptureStatusPanel`, `RecordingCard`), `routes/_app/live-session.tsx`, `routes/_app/notes/$noteId.tsx` |
| Shared | `packages/shared/src/lib/types.ts`, `lib/i18n/locales/pt.json` |
| Tests | `anamnys-aspire.Tests/LiveSession/AudioDestructionTests.cs`, `LiveRecordingModesTests.cs`, `TranscriptionHubTests.cs`, `Data/TransientAudioMigrationTests.cs` |

Limits and known gaps (full list in `design/gaps/2026-10-03-patient-flows-gaps.md`, section "Captura de áudio"):

- No direct capture of a call app's audio on mobile (covered by "pelo viva-voz"); no system audio on macOS (use the web
  version of the call or the speaker method); with the screen off on a phone the page is suspended (the recording pauses
  and resumes); no speaker separation in the transcript; no video or screen recording.
- No unit-test runner in the provider app: `audioCapture.ts` is untested apart from build, lint and the manual script.
- A resume that adds a source is not audited (only `SwitchSource` audits), and `SwitchSource` adds the source before it
  audits (not atomic; the client can retry).
- Hub test gaps: mode mismatch on a resume (tested on the buffers, not over a real hub), `forbidden` over a real hub,
  concurrent switches.
- A buffer that expired is re-opened by the client under its old id with whatever mode it sends; the consent gate runs
  again for a session, but a dictation-mode begin with the same id is accepted (recording ids are not remembered).
- The cue outside our tab needs an existing notification permission; without it only the title changes.
- Hidden page pauses only on mobile (decision above, to be confirmed by the user).
- Partial-join paths of a switch, and the exact-constraint retry for echo cancellation, were not exercised in a browser.
- A recording swept during a long pause or disconnection (or whose finalize failed) and then re-sent has more than one
  destruction row (`abandoned`/`failed`, then its final row); no keep-alive is sent. A finalize ack lost across a
  reconnect that is never retried ends with `abandoned` next to `transcribed`.
- Browser behaviour is verified only by the user's manual checks; the migration is not yet applied to the dev database.
