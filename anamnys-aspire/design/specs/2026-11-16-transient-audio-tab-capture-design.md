# Transient Audio Proof and Online Session Capture (G2: F-26 + F-25)

**Date:** 2026-11-16
**Status:** Approved for implementation
**Scope:** Backlog F-26 (transient audio pipeline: destruction recorded and proven) and F-25 (capture of the call's audio
for online sessions), the second delivery of Group G. G3 (F-27 pseudonymisation) and G4 (F-28 transcript as source)
get their own specs. Depends on G1 (recording consent, chapter 44): the capture rule there is unchanged.

Done when (backlog): F-26 — an integration test proves that no audio artefact survives anywhere after a completed
session, including the failure path; F-25 — the audio of an external meeting reaches our transcription pipeline.

## 1. Decisions (agreed with the user, 2026-11-15)

| Topic | Decision |
|---|---|
| What an online session records | Microphone (the provider) and the call's audio (the patient) mixed into one stream. |
| Choosing in-person / online | Sessão ao vivo gets "Presencial / Online" for the session mode, preselected from the linked appointment's `Modality` (otherwise "Presencial"); dictation is always microphone only. |
| Source of the call's audio | The browser tab of the call (Meet, Zoom web, Teams web) or, in Chrome/Edge on Windows, the entire screen with "share system audio" (covers the installed Zoom/Teams apps; warning that every sound of the computer is recorded). |
| Audio must always be recordable (user, 2026-11-15) | Desktop and mobile browsers. Besides the direct capture, an "Online pelo viva-voz" method: the call plays on the loudspeaker and the microphone records both voices, with the browser's echo cancellation OFF (it would erase the patient's voice). Available everywhere; the only online method on mobile and on desktop Firefox/Safari. |
| Unsupported direct capture | Where tab/screen capture is not available, "Online" offers only "pelo viva-voz", with the explanation "Neste navegador o áudio da chamada é captado pelo alto-falante. Deixe a chamada no viva-voz, sem fone." In-person keeps working everywhere. |
| Mobile robustness | The screen is kept awake during a recording (Screen Wake Lock); when the browser suspends the page (screen off, app switch) the recording pauses and, on return, a notice "A gravação foi pausada pelo navegador. Retomar?" continues the same recording (the server buffer keeps what was sent). |
| Share without audio | Recording does not start; the screen explains how to tick "Compartilhar áudio". |
| Sharing stops mid-session | Recording pauses at once with "Compartilhar de novo" (same recording continues) and "Encerrar gravação". Never silently microphone-only. |
| Prepared for Group H2 (user, 2026-11-15) | (a) The browser mixing takes pluggable sources (microphone, call tab/screen, speaker method; H2's own-room remote track later plugs in as one more source without touching the rest). (b) The method can be switched mid-recording within the same recording (e.g. tab capture fails → continue "pelo viva-voz"); this is the base of F-43 (switching from the own room to an external tool). The record keeps every source used. |
| Destruction record | Every recording (session and dictation) ends with an `AudioDestructionLogs` row: `transcribed`, `failed` or `abandoned`. |
| Where the provider sees it | The note page of a note that came from a recording shows "Áudio da sessão destruído em {data} às {hora}, após a transcrição" (or "…sem transcrição" when it failed). |

## 2. Server (F-26)

### 2.1 Today (verified 2026-11-15)

Chunks arrive through `TranscriptionHub`, live in `RecordingBuffers` (memory only, keyed by recording id), finalize
moves them into an in-memory `TranscriptionQueue` job, `NoteTranscription.ProcessAsync` transcribes and destroys the
audio in a `finally` (`Array.Clear` + `PcmAudio.Destroy` → `CryptographicOperations.ZeroMemory`);
`RecordingBufferSweeper` drops idle buffers. No audio goes to disk, the database, the object store or a durable queue.

### 2.2 Changes

- Migration `design/migrations/2026-11-16-transient-audio.sql` (idempotent, mirrored in `anamnys-db-script.sql`,
  migration-test chains after `2026-11-15-recording-consent.sql`). `AudioDestructionLogs` (schema only today: `Id`,
  `SessionId`, `DestroyedAt`, `Outcome` transcribed|failed) gains `ProviderId` uuid NOT NULL (FK), `NoteId` uuid null
  (FK `Notes`, on delete set null), `RecordingMode` `session|dictation`, `Sources` text[] — every source used, in
  order of first use, each one of `in_person|online_capture|online_speaker` (check by array containment; `{in_person}`
  for dictation); `Outcome` check adds `abandoned`; index on `NoteId`. `SessionId` holds the recording id. Audit action
  `audio_destroyed` (ids and outcome only). Discard deletes the patient's rows through their notes and provider
  (rows have no patient column: add `PatientId` uuid NOT NULL too, so discard and scoping are direct).
- `AudioDestruction.RecordAsync(db, recordingId, meta, noteId, outcome, now, ct)` writes the row and the audit entry.
  Called right after the audio is zeroed:
  - `NoteTranscription.ProcessAsync`: `transcribed` (text written), `failed` (exception or no speech);
  - `RecordingBuffers` sweep: `abandoned` — the sweeper collects the metas of the swept entries (memory zeroed under
    the lock) and records them outside the lock through a scoped `DbContext`;
  - an explicit cancel by the provider (if the hub has one): `abandoned`.
  Failures to write the row are logged (recording id and exception type only) and never keep audio alive.
- `RecordingMeta` gains `Mode` (`session|dictation`) and `Sources` (ordered set of
  `in_person|online_capture|online_speaker`). `BeginRecording` gains `string source` (the initial one; must be
  `in_person` for dictation; unknown → `invalid`); a resume adds its source if new. New hub method
  `SwitchSource(Guid recordingId, string source) : SwitchAck` (`ok|invalid|expired|forbidden`): own recording in
  progress, session mode only, adds the source to the meta's set and writes the audit action
  `recording_source_switched` (ids and source codes only). The audio chunks keep flowing to the same buffer; the
  sequence numbering is unchanged. The source goes to
  the meta, the destruction row and the existing begin audit; nothing else changes in the pipeline.
- Note view (`GET` note): `audioDestroyedAt`, `audioOutcome`, `audioSources` from the latest row for the note (null when
  the note did not come from a recording or predates this change).

### 2.3 Guarantees (chapter text, not code)

No durable queue or reprocessing keeps audio; a server restart loses in-memory audio with no destruction row and
nothing to recover; backups (F-46) copy the database and the object store, which hold no audio; the browser keeps
audio only in memory (no IndexedDB/localStorage).

## 3. Browser (F-25)

- `apps/provider/src/hooks/useLiveRecorder.ts` and a new module `apps/provider/src/lib/audioCapture.ts` built
  around pluggable sources: an `AudioSource` interface (`id`, `kind`, `start(ctx): Promise<AudioNode>`,
  `stop()`, `onEnded(cb)`, `level`) and a mixer that adds/removes sources on the running `AudioContext` chain
  without restarting the recording. Implementations: `microphoneSource(constraints)`, `displayAudioSource()` (tab or
  screen + system audio), and the speaker method as `microphoneSource` with echo cancellation off. "Trocar método"
  during an online recording pauses, swaps the call source (and the microphone constraints), calls `SwitchSource`,
  and resumes the same recording. H2 will add a `remoteTrackSource(track)`.
  - `captureSupport()` → `{ tab: boolean, systemAudio: boolean }` from `navigator.mediaDevices.getDisplayMedia`,
    user-agent data (Chromium desktop) and platform (Windows for system audio).
  - Online start: `getDisplayMedia({ video: true, audio: { echoCancellation: false, noiseSuppression: false },
    systemAudio: "include", selfBrowserSurface: "exclude", preferCurrentTab: false })`; if the returned stream has no
    audio track → stop every track, error `share_without_audio`. Video tracks are stopped immediately.
  - Mixing: microphone and call audio become two `MediaStreamAudioSourceNode`s into the existing `AudioContext`/
    `AudioWorkletNode` chain (a `GainNode` per source, summed, mono, the existing resampling to PCM); two level meters
    ("Você", "Chamada").
  - Share ended (`ended` on the audio track): pause the recording (the existing pause), state `share_lost`; "Compartilhar
    de novo" calls `getDisplayMedia` again and reconnects the call source to the same chain; "Encerrar gravação"
    finalizes.
  - Online pelo viva-voz (`online_speaker`): microphone only, `getUserMedia({ audio: { channelCount: 1,
    echoCancellation: false, noiseSuppression: false, autoGainControl: true } })`; one meter; banner "Deixe a chamada
    no viva-voz, sem fone".
  - Every recording (all sources and dictation): `navigator.wakeLock.request("screen")` while recording (re-requested
    on `visibilitychange` back to visible; released on stop); when the page becomes hidden or the audio context is
    suspended, the recording pauses (state `suspended`) and on return shows "A gravação foi pausada pelo navegador.
    Retomar?" — resume continues the same recording id (existing reconnect/resume path).
  - On stop/finalize/unmount every track of both streams is stopped and the nodes disconnected.
- `apps/provider/src/routes/_app/live-session.tsx` + `components/recordingConsent/SessionModeChoice.tsx` (or a sibling
  component): "Presencial / Online" for session mode (preselected from the selected appointment's modality); under
  Online, the method "Captar o áudio da chamada" (where supported, default) or "Pelo viva-voz" (always available; the
  only one where direct capture is unsupported, with its explanation); a short instruction before the browser picker (tab of the call and "Compartilhar
  áudio da aba"; on Windows also "ou a tela inteira com 'Compartilhar áudio do sistema' para o app do Zoom/Teams — todo
  som do computador será gravado; feche outros sons"); fixed banner during online recording "Informe o paciente
  verbalmente que a sessão está sendo gravada"; the two meters; the share-lost panel.
- Note page: the destruction line (section 1) and "sessão online" when `audioSources` contains `online_capture` or
  `online_speaker`.
- Share-lost panel and the online recording bar offer "Trocar método" (to "pelo viva-voz", or back to capture where
  supported).
- Copy in `pt.json` under `liveSession.online.*` and `notes.audio.*`.

## 4. Testing

- F-26 integration (tests first): for a completed recording, a transcriber failure, and a swept (abandoned) buffer —
  the buffer entry is gone and its bytes are zero (inspect through a test hook), no table has a `bytea` column other
  than the known non-audio ones (token hashes, etc.; assert by a whitelist), the object store has no new object,
  the process temp directory has no new file, and exactly one `AudioDestructionLogs` row with the right outcome,
  mode, source, provider, patient and note exists, with the `audio_destroyed` audit entry.
- Hub: `source` validation (online sources refused for dictation; unknown refused); sources stored on the meta and
  the row; `SwitchSource` adds a source, is audited, is refused for dictation, foreign or expired recordings, and the
  chunks before and after the switch end up in one transcription.
- Note view returns the destruction fields; discard removes the rows; scoping; migration and chains.
- Browser: unit tests for `audioCapture.ts` (support detection by UA/platform, no-audio share rejected, share-ended
  → pause state, speaker method constraints with echo cancellation off, mixer add/remove source without restarting,
  method switch keeps the recording id, suspend → pause → resume state, tracks
  stopped on stop). Provider build and lint. Manual checks: a real Meet call in Chrome (tab), the installed Zoom app
  on Windows (screen + system audio), and a phone browser with the call on speaker (recorded in the chapter).

## 5. Out of scope / gaps

Direct capture of a call app's audio on mobile (iOS/Android do not allow it — covered by "pelo viva-voz"); direct
capture of macOS desktop apps (no system audio in the browser on macOS — use the web version of the call or the
speaker method); recording while the phone's screen is off (browsers suspend the page — the recording pauses and
resumes); speaker separation in the transcript; recording video or the screen.

## 6. Documentation

Chapter `documentation/45-transient-audio-online-capture.md`; chapter 44 (live session modes) cross-reference; index;
status (F-25, F-26); gaps; Confluence (F-25, F-26).
