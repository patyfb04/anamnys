# Transient Audio Proof and Online Session Capture (G2: F-26 + F-25) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record and prove the destruction of every recording's audio, and record online sessions (call audio + microphone, or the speaker method) from desktop and mobile browsers, with the method switchable mid-recording.

**Architecture:** Server: `AudioDestructionLogs` written right after the audio is zeroed on every path (transcribed, failed, abandoned), recording meta carries mode and sources, a `SwitchSource` hub method; note view exposes the destruction fields. Browser: a pluggable-source mixer (`audioCapture.ts`) on the existing AudioContext/worklet chain, online methods, wake lock and suspend/resume, UI on Sessão ao vivo and a line on the note page.

**Tech Stack:** .NET 10, EF Core + Npgsql, SignalR, React 19 + TanStack, Web Audio, Screen Capture and Wake Lock APIs, i18next.

**Spec:** `design/specs/2026-11-16-transient-audio-tab-capture-design.md` (binding).

## Global Constraints

- Branch `feature/transient-audio`. **Never commit, stage, stash or switch branches.**
- Tests: `timeout 1500 dotnet run --project anamnys-aspire.Tests/anamnys-aspire.Tests.csproj -- -class "*X*"` from `anamnys-aspire/` (never `dotnet test`). One test process at a time; output to a file in the scratchpad `C:/Users/patri/AppData/Local/Temp/claude/cddb4cc4d594bfe5/13806727-9a85-492d-af33-bc5a7050064b/scratchpad/` and grep it. Fixture classes only when no AppHost/Server/aspire/Tests/dcp process runs; otherwise build with `--artifacts-path "$TEMP/anamnys-artifacts"` and report not run. If a run hangs over 25 minutes at AppHost startup, kill only the Tests process you started and report it. Never start/stop Aspire. Do not apply migrations to the dev DB. Scratch databases for new-schema tests.
- Change files only with Edit/Write (no shell commands that write, move or delete files; never perl/sed on files).
- Audio never written to disk, database, object store, logs or a durable queue; zeroed on every path; destruction row and audit carry ids, codes and outcome only.
- Provider scoping; 404 for foreign ids; 401 first; `no-store`.
- The G1 capture rule (chapter 44) is unchanged: session recording still requires a valid consent.
- Copy in `pt.json` (`liveSession.online.*`, `notes.audio.*`); `npm run build` / `npm run lint` pass in the provider app.

---

### Task 1: Server — destruction record and proof (F-26)

**Files:** `design/migrations/2026-11-16-transient-audio.sql` (spec §2.2: `AudioDestructionLogs` gains `ProviderId`, `PatientId`, `NoteId`, `RecordingMode`, `Sources` text[], outcome `abandoned`, index; audit action `audio_destroyed`) + `anamnys-db-script.sql` + migration-test chains (after `2026-11-15-recording-consent.sql`); entity/mapping; create `anamnys-aspire.Server/LiveSession/AudioDestruction.cs`; modify `RecordingBuffers.cs` (meta `Mode`, `Sources`; sweep returns the swept metas after zeroing), `RecordingBufferSweeper.cs` (records `abandoned` through a scoped DbContext), `NoteTranscription.cs` (records `transcribed`/`failed` after zeroing), `TranscriptionQueue`/job (carry the meta needed), note view contract (`audioDestroyedAt`, `audioOutcome`, `audioSources`), `Retention/DiscardService.cs`; tests `anamnys-aspire.Tests/LiveSession/AudioDestructionTests.cs` (the F-26 integration proof, tests first), `Data/TransientAudioMigrationTests.cs`.

**Interfaces (produced):**
- `RecordingMeta(Guid ProviderId, Guid PatientId, Guid? AppointmentId, string Format, string Language, DateTimeOffset StartedAt, string Mode, IReadOnlyList<string> Sources)` (keep existing positional order, append the new ones).
- `AudioDestruction.RecordAsync(AnamnysDbContext db, Guid recordingId, RecordingMeta meta, Guid? noteId, string outcome, DateTimeOffset now, CancellationToken ct) : Task`.
- `RecordingBuffers.SweepExpired() : IReadOnlyList<(Guid RecordingId, RecordingMeta Meta)>`.

- [ ] Spec §2 and §4 server bullets for destruction; for now `BeginRecording` passes `Mode` from the existing mode argument and `Sources = ["in_person"]` (Task 2 adds the source argument).

### Task 2: Server — sources and switching

**Files:** `LiveSession/TranscriptionHub.cs` (`BeginRecording(..., string mode, bool dictationConfirmed, string source)`; new `SwitchSource(Guid recordingId, string source) : SwitchAck`), `LiveSession/LiveRecordings.cs`, `LiveSession/RecordingBuffers.cs` (add a source to an entry), `LiveSessionContracts.cs` (`SwitchAck(string Status)` with `ok|invalid|expired|forbidden`), audit action `recording_source_switched` in the migration (same file as Task 1) and script; tests in `anamnys-aspire.Tests/LiveSession/*`.

**Interfaces:** Consumes Task 1's `RecordingMeta` and buffers. Produces the 8-argument `BeginRecording` and `SwitchSource`.

- [ ] Spec §2.2 sources/switch bullets and §4 hub bullets; tests first.

### Task 3: Browser — capture, mixer, UI

**Files:** create `apps/provider/src/lib/audioCapture.ts` (+ `audioCapture.test.ts` if the provider app has a test runner; otherwise keep the logic pure and document manual checks), modify `apps/provider/src/hooks/useLiveRecorder.ts`, `apps/provider/src/lib/signalr.ts` (8-arg begin, `switchSource`), `apps/provider/src/routes/_app/live-session.tsx`, `components/recordingConsent/SessionModeChoice.tsx` or a new `components/live-session/OnlineMethodChoice.tsx`, the note page (`routes/_app/notes/$noteId.tsx`) for the destruction line, shared note types, `pt.json`.

**Interfaces:** Consumes Task 2's hub signatures and Task 1's note fields.

- [ ] Spec §3. Build and lint.

### Task 4: Documentation

**Files:** `documentation/45-transient-audio-online-capture.md`, `documentation/44-recording-consent.md` (cross-reference in the live-session section), `00-index.md`, `design/status/2026-10-15-implementation-status.md` (F-25, F-26 rows; verification line), `design/gaps/2026-10-03-patient-flows-gaps.md` (spec §5).

- [ ] Spec §6 (Confluence by the controller).
