# Live Session — Transcription Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The provider dictates in the browser on `/live-session`; PCM audio streams over SignalR, Whisper.net transcribes it in-process when the recording stops, and the transcript lands on a `VoiceLive` note attached to a `Sessions` row, shown on `/notes/$noteId`.

**Architecture:** A `TranscriptionHub` (providers cookie only) feeds an in-memory `RecordingBuffers` singleton keyed by recording id; `FinalizeStream` writes the session and the `Processing` note in one transaction and hands the audio to an in-memory `TranscriptionQueue`. A single `TranscriptionWorker` runs `NoteTranscription.ProcessAsync` through an `ITranscriber` (Whisper.net, CPU), always destroys the audio, and pushes progress through a `ProgressHub`. The browser captures PCM 16 kHz mono via an AudioWorklet and keeps every chunk until finalize is acknowledged, resending after a reconnect.

**Tech Stack:** .NET 10, ASP.NET Core SignalR, EF Core 10 + Npgsql, PostgreSQL 18, Whisper.net (CPU runtime), xUnit v3 + FluentAssertions; React 19, TanStack Router/Query, Tailwind 4, i18next, `@microsoft/signalr`.

**Spec:** `design/specs/2026-10-10-live-session-transcription-design.md`

## Global Constraints

- Audio lives in server memory only, keyed by recording id. Never disk, Redis, a durable queue, a log or a backup. Destroyed (zeroed) after transcription, on failure, on timeout and on restart. Logs never carry transcript text or audio.
- Audio format on the wire: PCM 16 kHz mono 16-bit little-endian, base64 in the JSON hub protocol, ~16 KB raw per chunk.
- Limits: 90 minutes of audio per recording (`90 * 60 * 16000 * 2 = 172_800_000` bytes); client warns at 85 minutes and finalizes at 90; a buffer with no chunk for 10 minutes is discarded.
- Formats: `DAP`, `SOAP`, `Biopsychosocial`. Languages: `pt`, `en`, `es` (default `pt`).
- Note statuses written here: `Processing`, `ReadyForReview`, `Failed`. `FailureReason`: `no_speech`, `transcription_error`, `server_restart`; null unless `Failed`.
- Every query is provider-scoped by the id resolved from the providers cookie (`ClaimsPrincipal.LocalId()` inside the hub, `PatientRecordEndpoints.ProviderIdAsync(http)` in endpoints).
- Hubs accept only `AuthSchemes.ProviderCookie`; any other realm or no session gets 401.
- Whisper: CPU only (`Whisper.net.Runtime`). Model from `LiveSession:Model` (`base` in development), verified against `LiveSession:ModelSha256`, stored in `LiveSession:ModelDirectory`.
- One container: no SignalR backplane.
- All UI strings in Portuguese in `packages/shared/src/lib/i18n/locales/pt.json`.
- Tests: `dotnet run --project anamnys-aspire.Tests -- -class "*Name*"` from `anamnys-aspire/`; **never `dotnet test`**; `aspire stop` before building if the app runs. Frontend: `npm run build` and `npm run lint` from `anamnys-aspire/`. No frontend test runner exists.
- The test fixture shares the dev Postgres volume: apply the migration to the dev database (Task 1) before running Postgres tests.
- **Do not commit until the user has seen the changes** (user rule). Each task's last step stages the files and stops; the user commits or authorises the commit.
- `routeTree.gen.ts` must stay committed and in sync (CLAUDE.md gotcha); regenerate with `npx vite build` in `apps/provider` after adding routes.

---

## File Structure

**Database:** `design/migrations/2026-10-10-live-session.sql` (new), `anamnys-db-script.sql` (same changes).

**Server (`anamnys-aspire.Server/`)**
- `Data/Entities/Note.cs` (more columns), `Data/Entities/Session.cs` (new), `Data/Entities/Appointment.cs` (`SessionId`), `Data/AnamnysDbContext.cs` (`Sessions` set + mapping).
- `LiveSession/LiveSessionRules.cs` — constants and validation of format, language, limits.
- `LiveSession/RecordingBuffers.cs` — in-memory buffers (no DB, no DI dependencies besides `TimeProvider`).
- `LiveSession/PcmAudio.cs` — owned byte buffer with `ToSamples()` and `Destroy()`.
- `LiveSession/TranscriptionQueue.cs` — `Channel` wrapper with queue positions.
- `LiveSession/LiveRecordings.cs` — begin validation, finalize transaction, orphan sweep.
- `LiveSession/ITranscriber.cs`, `LiveSession/WhisperTranscriber.cs`, `LiveSession/WhisperModelProvider.cs`.
- `LiveSession/NoteTranscription.cs` — one job: transcribe, write the note, destroy audio.
- `LiveSession/TranscriptionWorker.cs`, `LiveSession/RecordingBufferSweeper.cs`, `LiveSession/OrphanNoteSweep.cs` — hosted services.
- `LiveSession/TranscriptionHub.cs`, `LiveSession/ProgressHub.cs`, `LiveSession/LiveSessionContracts.cs`.
- `LiveSession/LiveSessionSetup.cs` — `AddLiveSession()` / `MapLiveSessionHubs()`.
- `Notes/NoteEndpoints.cs`, `Notes/NoteQueries.cs` — `GET /api/phi/providers/me/notes/{noteId}`.
- `Program.cs`, `appsettings.json`, `appsettings.Development.json`, `anamnys-aspire.Server.csproj`, `anamnys-aspire.Server.http`.

**Tests (`anamnys-aspire.Tests/`)**: `Data/LiveSessionMigrationTests.cs`, `LiveSession/LiveSessionRulesTests.cs`, `LiveSession/RecordingBuffersTests.cs`, `LiveSession/PcmAudioTests.cs`, `LiveSession/LiveRecordingsTests.cs`, `LiveSession/NoteTranscriptionTests.cs`, `LiveSession/FakeTranscriber.cs`, `LiveSession/WhisperTranscriberTests.cs`, `LiveSession/TranscriptionHubTests.cs`, `Notes/NoteEndpointTests.cs`; `anamnys-aspire.Tests.csproj` (SignalR client package).

**Frontend**
- `packages/shared/src/lib/types.ts` (`NoteDetail`, live-session types), `packages/shared/src/api/notes.ts` (`getDetail`).
- `apps/provider/src/lib/signalr.ts` (rewritten), `apps/provider/public/worklets/pcm16-recorder.js` (new), `apps/provider/src/lib/chunkLog.ts` (new), `apps/provider/src/hooks/useLiveRecorder.ts` (new).
- `apps/provider/src/routes/_app/live-session.tsx` (new), `apps/provider/src/components/live-session/{PrepareStep,RecordingStep,ProcessingStep}.tsx` (new).
- `apps/provider/src/routes/_app/notes/$noteId.tsx` (status + transcript).
- `packages/shared/src/lib/i18n/locales/pt.json`.

**Docs:** `design/gaps/2026-10-03-patient-flows-gaps.md`, `CLAUDE.md` (tech stack: Whisper.net present).

---

### Task 1: Schema, migration and entities

**Files:**
- Create: `anamnys-aspire/design/migrations/2026-10-10-live-session.sql`
- Modify: `anamnys-aspire/anamnys-db-script.sql` (the `Notes` table)
- Create: `anamnys-aspire/anamnys-aspire.Server/Data/Entities/Session.cs`
- Modify: `anamnys-aspire/anamnys-aspire.Server/Data/Entities/Note.cs`, `Appointment.cs`, `Data/AnamnysDbContext.cs`
- Test: `anamnys-aspire/anamnys-aspire.Tests/Data/LiveSessionMigrationTests.cs`

**Interfaces:**
- Produces: `Notes."FailureReason"`, `Notes."RecordingId"` (unique index `Notes_RecordingId_key`), `'Failed'` in `Notes_Status_ck`, `Notes_FailureReason_ck`; `Note` properties `SessionId`, `InputMode`, `RawTranscript`, `RecordingId`, `FailureReason`, `UpdatedAt`; `Session` entity; `Appointment.SessionId`; `DbSet<Session> Sessions`.

- [ ] **Step 1: Read the patterns.** `design/migrations/2026-10-07-appointment-notifications.sql` (header, `BEGIN … COMMIT`, idempotency), `anamnys-aspire.Tests/Data/NotificationsMigrationTests.cs`, and the `Notes` and `Sessions` definitions in `anamnys-db-script.sql`.

- [ ] **Step 2: Write the failing migration test** — `Data/LiveSessionMigrationTests.cs`:

```csharp
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-10-live-session.sql against the previous schema and the bootstrap
// script (design/specs/2026-10-10-live-session-transcription-design.md §3).
[Collection(SharedAppHostCollection.Name)]
public class LiveSessionMigrationTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LiveSessionMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
    {
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using (var db = await old.OpenAsync(Ct))
        {
            await RunMigrationAsync(db, "2026-10-03-patient-accounts.sql");
            await RunMigrationAsync(db, "2026-10-04-patient-invitations.sql");
            await RunMigrationAsync(db, "2026-10-07-appointment-notifications.sql");
            await RunMigrationAsync(db, "2026-10-10-live-session.sql");
            await RunMigrationAsync(db, "2026-10-10-live-session.sql");
        }
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using (var db = await fresh.OpenAsync(Ct))
        {
            await RunMigrationAsync(db, "2026-10-10-live-session.sql");
        }

        var oldShape = await ShapeAsync(old);
        var freshShape = await ShapeAsync(fresh);
        oldShape.Should().BeEquivalentTo(freshShape);
        freshShape.Should().Contain(["Notes.FailureReason", "Notes.RecordingId", "Notes:Notes_RecordingId_key"]);
        freshShape.Should().Contain(s => s.StartsWith("\"Notes\":Notes_Status_ck") && s.Contains("'Failed'"));
    }

    [Fact]
    public async Task FailedNote_WithoutReason_IsRejected()
    {
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            WITH p AS (INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name")
                       VALUES (gen_random_uuid(), 'x@test.local', gen_random_uuid(), 'X') RETURNING "Id"),
                 pa AS (INSERT INTO "Patients" ("ProviderId","FirstName","LastName")
                        SELECT "Id", 'A', 'B' FROM p RETURNING "Id", "ProviderId")
            INSERT INTO "Notes" ("PatientId","ProviderId","Status","InputMode")
            SELECT "Id", "ProviderId", 'Failed', 'VoiceLive' FROM pa
            """, db);

        var act = () => command.ExecuteNonQueryAsync(Ct);

        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("Notes_FailureReason_ck");
    }

    private static async Task RunMigrationAsync(NpgsqlConnection db, string file)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", file), Ct);
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<string>> ShapeAsync(ScratchDatabase scratch)
    {
        await using var db = await scratch.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT table_name || '.' || column_name
              FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = ANY (@tables)
            UNION ALL
            SELECT c.conrelid::regclass::text || ':' || c.conname || ' ' || pg_get_constraintdef(c.oid)
              FROM pg_constraint c WHERE c.conrelid::regclass::text = ANY (@quoted)
            UNION ALL
            SELECT tablename || ':' || indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = ANY (@tables)
            """, db);
        string[] tables = ["Notes", "Sessions"];
        command.Parameters.AddWithValue("tables", tables);
        command.Parameters.AddWithValue("quoted", tables.Select(t => $"\"{t}\"").ToArray());
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var shape = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            shape.Add(reader.GetString(0));
        }
        return shape;
    }
}
```

If the `Patients` insert in the second test fails on another NOT NULL column, add that column with a literal value; check the `Patients` definition in `anamnys-db-script.sql`.

- [ ] **Step 3: Run it to verify it fails.**
Run: `dotnet run --project anamnys-aspire.Tests -- -class "*LiveSessionMigrationTests*"`
Expected: FAIL — `Migrations/2026-10-10-live-session.sql` not found.

- [ ] **Step 4: Write the migration** — `design/migrations/2026-10-10-live-session.sql`:

```sql
-- Live session transcription: failed notes and idempotent finalize
-- (design/specs/2026-10-10-live-session-transcription-design.md §3). Run after
-- 2026-10-07-appointment-notifications.sql. Idempotent, and all-or-nothing.
--
--   docker exec -i <postgres container> sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d anamnysdb -v ON_ERROR_STOP=1' < design/migrations/2026-10-10-live-session.sql

BEGIN;

ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "FailureReason" text;
ALTER TABLE "Notes" ADD COLUMN IF NOT EXISTS "RecordingId" uuid;
CREATE UNIQUE INDEX IF NOT EXISTS "Notes_RecordingId_key" ON "Notes" ("RecordingId");

ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_Status_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_Status_ck" CHECK (("Status" = ANY (ARRAY['Draft'::text, 'Processing'::text, 'ReadyForReview'::text, 'Signed'::text, 'Exported'::text, 'Failed'::text])));

ALTER TABLE "Notes" DROP CONSTRAINT IF EXISTS "Notes_FailureReason_ck";
ALTER TABLE "Notes" ADD CONSTRAINT "Notes_FailureReason_ck" CHECK ((("Status" = 'Failed'::text) = ("FailureReason" IS NOT NULL)) AND (("FailureReason" IS NULL) OR ("FailureReason" = ANY (ARRAY['no_speech'::text, 'transcription_error'::text, 'server_restart'::text]))));

COMMIT;
```

- [ ] **Step 5: Mirror it in `anamnys-db-script.sql`.** In `CREATE TABLE "Notes"`: add `"FailureReason" text,` and `"RecordingId" uuid,` after `"UpdatedAt"`; add `'Failed'::text` to `Notes_Status_ck`; add the `Notes_FailureReason_ck` constraint with the exact expression above. Next to the other `CREATE INDEX` lines add `CREATE UNIQUE INDEX "Notes_RecordingId_key" ON "Notes" ("RecordingId");`.

- [ ] **Step 6: Apply the migration to the dev database** (the fixture shares it). With the app running (`aspire start`), find the Postgres container with `docker ps --filter name=postgres` and run the `psql` command from the migration header.

- [ ] **Step 7: Run the test to verify it passes.** Same command. Expected: PASS (2 tests).

- [ ] **Step 8: Entities.** `Data/Entities/Session.cs`:

```csharp
namespace Anamnys.Server.Data.Entities;

// Column subset of "Sessions": what happened, as opposed to the appointment (what was planned).
// Created by live session finalize (design/specs/2026-10-10-live-session-transcription-design.md §5).
public class Session
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public DateTimeOffset? ScheduledStart { get; set; }
    public int? ScheduledMinutes { get; set; }
    public DateTimeOffset? ActualStart { get; set; }
    public DateTimeOffset? ActualEnd { get; set; }
    public string? Modality { get; set; }
    public string Attendance { get; set; } = "scheduled";
    public DateTimeOffset CreatedAt { get; set; }
}
```

`Note.cs` gains (keep the existing properties):

```csharp
    public Guid? SessionId { get; set; }
    public string InputMode { get; set; } = "";
    public string? RawTranscript { get; set; }
    public Guid? RecordingId { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
```

`Appointment.cs` gains `public Guid? SessionId { get; set; }` and its header comment drops "session" from the unmapped list.

`AnamnysDbContext.cs`: `public DbSet<Session> Sessions => Set<Session>();` and

```csharp
        modelBuilder.Entity<Session>(e =>
        {
            e.ToTable("Sessions");
            e.HasKey(x => x.Id);
        });
```

Check where `Note` rows are created today (`grep -rn "new Note" anamnys-aspire.Server anamnys-aspire.Tests`): with `InputMode` now mapped, every insert through the entity must set it (`"Text"` matches the test seed); `CreatedAt`/`UpdatedAt` must be set explicitly because EF sends the CLR default.

- [ ] **Step 9: Build and run the existing suites touching notes and appointments.**
Run: `dotnet run --project anamnys-aspire.Tests -- -class "*Patient*" -class "*Appointment*"`
Expected: PASS.

- [ ] **Step 10: Stage for review.** `git add` the files above; do not commit (Global Constraints).

---

### Task 2: Rules, PCM audio and recording buffers

**Files:**
- Create: `anamnys-aspire.Server/LiveSession/LiveSessionRules.cs`, `LiveSession/PcmAudio.cs`, `LiveSession/RecordingBuffers.cs`
- Test: `anamnys-aspire.Tests/LiveSession/LiveSessionRulesTests.cs`, `PcmAudioTests.cs`, `RecordingBuffersTests.cs`

**Interfaces:**
- Produces:
  - `LiveSessionRules`: `const int SampleRate = 16000`, `const long MaxAudioBytes = 172_800_000`, `static readonly TimeSpan BufferIdleTimeout = TimeSpan.FromMinutes(10)`, `static readonly string[] Formats = ["DAP","SOAP","Biopsychosocial"]`, `static readonly string[] Languages = ["pt","en","es"]`, `static bool IsValid(string format, string language)`.
  - `PcmAudio(byte[] bytes, int length)`: `int Length`, `float[] ToSamples()`, `void Destroy()`.
  - `RecordingMeta(Guid ProviderId, Guid PatientId, Guid? AppointmentId, string Format, string Language, DateTimeOffset StartedAt)`.
  - `enum ChunkStatus { Accepted, Duplicate, Gap, Expired, Forbidden, TooLong }`; `record ChunkResult(ChunkStatus Status, int LastSeq)`.
  - `enum BeginStatus { Opened, Resumed, Forbidden }`.
  - `record TakenRecording(RecordingMeta Meta, PcmAudio Audio)`.
  - `RecordingBuffers(TimeProvider clock)`: `BeginStatus Begin(Guid recordingId, RecordingMeta meta)`, `ChunkResult Append(Guid recordingId, Guid providerId, int seq, ReadOnlySpan<byte> chunk)`, `RecordingMeta? Peek(Guid recordingId, Guid providerId)`, `TakenRecording? Take(Guid recordingId, Guid providerId)`, `int SweepExpired()`.

Sequence numbers start at 0. `LastSeq` is `-1` before any chunk.

- [ ] **Step 1: Write the failing tests.**

`LiveSessionRulesTests.cs`:

```csharp
using Anamnys.Server.LiveSession;
using FluentAssertions;

namespace Anamnys.Tests.LiveSession;

public class LiveSessionRulesTests
{
    [Theory]
    [InlineData("DAP", "pt", true)]
    [InlineData("SOAP", "en", true)]
    [InlineData("Biopsychosocial", "es", true)]
    [InlineData("PtFunctional", "pt", false)]
    [InlineData("DAP", "fr", false)]
    [InlineData("dap", "pt", false)]
    public void IsValid_FormatAndLanguage_MatchesTheAllowedLists(string format, string language, bool expected)
    {
        LiveSessionRules.IsValid(format, language).Should().Be(expected);
    }

    [Fact]
    public void MaxAudioBytes_Is90MinutesOfPcm16kMono()
    {
        LiveSessionRules.MaxAudioBytes.Should().Be(90L * 60 * 16000 * 2);
    }
}
```

`PcmAudioTests.cs`:

```csharp
using Anamnys.Server.LiveSession;
using FluentAssertions;

namespace Anamnys.Tests.LiveSession;

public class PcmAudioTests
{
    [Fact]
    public void ToSamples_LittleEndianInt16_ScalesToMinusOneToOne()
    {
        // 0, 16384, -32768, 32767
        var audio = new PcmAudio([0x00, 0x00, 0x00, 0x40, 0x00, 0x80, 0xFF, 0x7F, 0xAA], length: 8);

        var samples = audio.ToSamples();

        samples.Should().Equal([0f, 0.5f, -1f, 32767f / 32768f]);
    }

    [Fact]
    public void Destroy_ZeroesTheBufferAndEmptiesIt()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var audio = new PcmAudio(bytes, 4);

        audio.Destroy();

        bytes.Should().OnlyContain(b => b == 0);
        audio.Length.Should().Be(0);
        audio.ToSamples().Should().BeEmpty();
    }
}
```

`RecordingBuffersTests.cs`:

```csharp
using Anamnys.Server.LiveSession;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.LiveSession;

public class RecordingBuffersTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 13, 0, 0, TimeSpan.Zero);
    private static readonly Guid Provider = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static RecordingMeta Meta(Guid provider) => new(provider, Guid.NewGuid(), null, "DAP", "pt", Start);

    [Fact]
    public void Append_InOrder_AcceptsAndTake_ReturnsAllBytes()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider)).Should().Be(BeginStatus.Opened);

        buffers.Append(id, Provider, 0, [1, 2]).Should().Be(new ChunkResult(ChunkStatus.Accepted, 0));
        buffers.Append(id, Provider, 1, [3, 4]).Should().Be(new ChunkResult(ChunkStatus.Accepted, 1));
        var taken = buffers.Take(id, Provider);

        taken.Should().NotBeNull();
        taken!.Audio.Length.Should().Be(4);
        taken.Audio.ToSamples().Should().HaveCount(2);
        buffers.Take(id, Provider).Should().BeNull();
    }

    [Fact]
    public void Append_RepeatedSeq_IsDuplicateAndNotAppended()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider));
        buffers.Append(id, Provider, 0, [1, 2]);

        buffers.Append(id, Provider, 0, [1, 2]).Should().Be(new ChunkResult(ChunkStatus.Duplicate, 0));

        buffers.Take(id, Provider)!.Audio.Length.Should().Be(2);
    }

    [Fact]
    public void Append_SkippedSeq_IsGap()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider));

        buffers.Append(id, Provider, 1, [1, 2]).Should().Be(new ChunkResult(ChunkStatus.Gap, -1));
    }

    [Fact]
    public void Append_UnknownRecording_IsExpired()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));

        buffers.Append(Guid.NewGuid(), Provider, 0, [1, 2]).Status.Should().Be(ChunkStatus.Expired);
    }

    [Fact]
    public void OtherProvider_CannotBeginAppendPeekOrTake()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider));

        buffers.Begin(id, Meta(Other)).Should().Be(BeginStatus.Forbidden);
        buffers.Append(id, Other, 0, [1, 2]).Status.Should().Be(ChunkStatus.Forbidden);
        buffers.Peek(id, Other).Should().BeNull();
        buffers.Take(id, Other).Should().BeNull();
        buffers.Take(id, Provider).Should().NotBeNull();
    }

    [Fact]
    public void Begin_AgainBySameProvider_ResumesAndKeepsAudio()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider));
        buffers.Append(id, Provider, 0, [1, 2]);

        buffers.Begin(id, Meta(Provider)).Should().Be(BeginStatus.Resumed);

        buffers.Append(id, Provider, 1, [3, 4]).Status.Should().Be(ChunkStatus.Accepted);
    }

    [Fact]
    public void Append_BeyondMaxAudio_IsTooLong()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start), maxAudioBytes: 4);
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider));
        buffers.Append(id, Provider, 0, [1, 2, 3, 4]);

        buffers.Append(id, Provider, 1, [5, 6]).Should().Be(new ChunkResult(ChunkStatus.TooLong, 0));
    }

    [Fact]
    public void SweepExpired_After10IdleMinutes_DiscardsAndZeroes()
    {
        var clock = new FakeTimeProvider(Start);
        var buffers = new RecordingBuffers(clock);
        var idle = Guid.NewGuid();
        var active = Guid.NewGuid();
        buffers.Begin(idle, Meta(Provider));
        buffers.Begin(active, Meta(Provider));
        clock.Advance(TimeSpan.FromMinutes(9));
        buffers.Append(active, Provider, 0, [1, 2]);
        clock.Advance(TimeSpan.FromMinutes(2));

        buffers.SweepExpired().Should().Be(1);

        buffers.Peek(idle, Provider).Should().BeNull();
        buffers.Peek(active, Provider).Should().NotBeNull();
    }
}
```

Add `Microsoft.Extensions.TimeProvider.Testing` to the test project if it is not referenced yet: `dotnet add anamnys-aspire.Tests package Microsoft.Extensions.TimeProvider.Testing` (check `grep -rn FakeTimeProvider anamnys-aspire.Tests` first — the notification tests may already use it).

- [ ] **Step 2: Run to verify failure.**
Run: `dotnet run --project anamnys-aspire.Tests -- -class "*LiveSessionRulesTests*" -class "*PcmAudioTests*" -class "*RecordingBuffersTests*"`
Expected: build FAIL — namespace `Anamnys.Server.LiveSession` not found.

- [ ] **Step 3: Implement.**

`LiveSession/LiveSessionRules.cs`:

```csharp
namespace Anamnys.Server.LiveSession;

// design/specs/2026-10-10-live-session-transcription-design.md §2 and §4.1.
public static class LiveSessionRules
{
    public const int SampleRate = 16000;
    public const long MaxAudioBytes = 90L * 60 * SampleRate * 2;
    public static readonly TimeSpan BufferIdleTimeout = TimeSpan.FromMinutes(10);
    public static readonly string[] Formats = ["DAP", "SOAP", "Biopsychosocial"];
    public static readonly string[] Languages = ["pt", "en", "es"];

    public static bool IsValid(string format, string language) =>
        Formats.Contains(format, StringComparer.Ordinal) && Languages.Contains(language, StringComparer.Ordinal);
}
```

`LiveSession/PcmAudio.cs`:

```csharp
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Anamnys.Server.LiveSession;

// PCM 16 kHz mono 16-bit little-endian, owned by whoever holds it. Destroy zeroes the bytes:
// session audio never outlives its transcription (spec §2).
public sealed class PcmAudio(byte[] bytes, int length)
{
    private byte[] _bytes = bytes;

    public int Length { get; private set; } = length;

    public float[] ToSamples()
    {
        var samples = new float[Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(_bytes.AsSpan(i * 2, 2)) / 32768f;
        }
        return samples;
    }

    public void Destroy()
    {
        CryptographicOperations.ZeroMemory(_bytes);
        _bytes = [];
        Length = 0;
    }
}
```

`LiveSession/RecordingBuffers.cs`:

```csharp
using System.Security.Cryptography;

namespace Anamnys.Server.LiveSession;

public sealed record RecordingMeta(Guid ProviderId, Guid PatientId, Guid? AppointmentId, string Format, string Language, DateTimeOffset StartedAt);

public enum BeginStatus { Opened, Resumed, Forbidden }

public enum ChunkStatus { Accepted, Duplicate, Gap, Expired, Forbidden, TooLong }

public sealed record ChunkResult(ChunkStatus Status, int LastSeq);

public sealed record TakenRecording(RecordingMeta Meta, PcmAudio Audio);

// In-memory audio of recordings in progress, keyed by recording id so a reconnect continues the
// same buffer (spec §4.2). Never written anywhere else.
public sealed class RecordingBuffers(TimeProvider clock, long maxAudioBytes = LiveSessionRules.MaxAudioBytes)
{
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Lock _lock = new();

    public BeginStatus Begin(Guid recordingId, RecordingMeta meta)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(recordingId, out var existing))
            {
                if (existing.Meta.ProviderId != meta.ProviderId)
                {
                    return BeginStatus.Forbidden;
                }
                existing.Touch(clock.GetUtcNow());
                return BeginStatus.Resumed;
            }
            _entries[recordingId] = new Entry(meta, clock.GetUtcNow());
            return BeginStatus.Opened;
        }
    }

    public ChunkResult Append(Guid recordingId, Guid providerId, int seq, ReadOnlySpan<byte> chunk)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(recordingId, out var entry))
            {
                return new ChunkResult(ChunkStatus.Expired, -1);
            }
            if (entry.Meta.ProviderId != providerId)
            {
                return new ChunkResult(ChunkStatus.Forbidden, -1);
            }
            if (seq <= entry.LastSeq)
            {
                return new ChunkResult(ChunkStatus.Duplicate, entry.LastSeq);
            }
            if (seq != entry.LastSeq + 1)
            {
                return new ChunkResult(ChunkStatus.Gap, entry.LastSeq);
            }
            if (entry.Length + chunk.Length > maxAudioBytes)
            {
                return new ChunkResult(ChunkStatus.TooLong, entry.LastSeq);
            }
            entry.Write(chunk);
            entry.LastSeq = seq;
            entry.Touch(clock.GetUtcNow());
            return new ChunkResult(ChunkStatus.Accepted, seq);
        }
    }

    public RecordingMeta? Peek(Guid recordingId, Guid providerId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(recordingId, out var entry) && entry.Meta.ProviderId == providerId ? entry.Meta : null;
        }
    }

    public TakenRecording? Take(Guid recordingId, Guid providerId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(recordingId, out var entry) || entry.Meta.ProviderId != providerId)
            {
                return null;
            }
            _entries.Remove(recordingId);
            return new TakenRecording(entry.Meta, entry.Detach());
        }
    }

    public int SweepExpired()
    {
        var cutoff = clock.GetUtcNow() - LiveSessionRules.BufferIdleTimeout;
        lock (_lock)
        {
            var expired = _entries.Where(kv => kv.Value.LastActivity < cutoff).Select(kv => kv.Key).ToList();
            foreach (var id in expired)
            {
                _entries[id].Detach().Destroy();
                _entries.Remove(id);
            }
            return expired.Count;
        }
    }

    private sealed class Entry(RecordingMeta meta, DateTimeOffset now)
    {
        private byte[] _bytes = new byte[64 * 1024];

        public RecordingMeta Meta { get; } = meta;
        public int LastSeq { get; set; } = -1;
        public int Length { get; private set; }
        public DateTimeOffset LastActivity { get; private set; } = now;

        public void Touch(DateTimeOffset at) => LastActivity = at;

        public void Write(ReadOnlySpan<byte> chunk)
        {
            if (Length + chunk.Length > _bytes.Length)
            {
                var grown = new byte[Math.Max(_bytes.Length * 2, Length + chunk.Length)];
                _bytes.AsSpan(0, Length).CopyTo(grown);
                CryptographicOperations.ZeroMemory(_bytes);
                _bytes = grown;
            }
            chunk.CopyTo(_bytes.AsSpan(Length));
            Length += chunk.Length;
        }

        public PcmAudio Detach()
        {
            var audio = new PcmAudio(_bytes, Length);
            _bytes = [];
            Length = 0;
            return audio;
        }
    }
}
```

Note: `maxAudioBytes` is `long` while `Length` is `int`; 172.8 MB fits in `int`.

- [ ] **Step 4: Run tests to verify they pass.** Same command. Expected: PASS.

- [ ] **Step 5: Stage for review.**

---

### Task 3: Queue, begin validation and finalize

**Files:**
- Create: `anamnys-aspire.Server/LiveSession/TranscriptionQueue.cs`, `LiveSession/LiveRecordings.cs`
- Test: `anamnys-aspire.Tests/LiveSession/LiveRecordingsTests.cs`

**Interfaces:**
- Consumes: Task 1 entities; Task 2 `RecordingBuffers`, `RecordingMeta`, `TakenRecording`, `PcmAudio`, `LiveSessionRules`.
- Produces:
  - `record TranscriptionJob(Guid NoteId, Guid ProviderId, string Language, PcmAudio Audio)`.
  - `TranscriptionQueue`: `void Enqueue(TranscriptionJob job)`, `ChannelReader<TranscriptionJob> Reader`, `int PositionOf(Guid noteId)` (1-based, 0 when not queued), `void MarkStarted(Guid noteId)`.
  - `enum BeginCheck { Ok, Invalid, NotFound }`.
  - `LiveRecordings.CheckBeginAsync(AnamnysDbContext db, Guid providerId, Guid patientId, Guid? appointmentId, string format, string language, CancellationToken ct) → Task<BeginCheck>`.
  - `enum FinalizeStatus { Ok, Expired }`; `record FinalizeResult(FinalizeStatus Status, Guid? NoteId)`.
  - `LiveRecordings.FinalizeAsync(AnamnysDbContext db, RecordingBuffers buffers, TranscriptionQueue queue, Guid providerId, Guid recordingId, DateTimeOffset now, CancellationToken ct) → Task<FinalizeResult>`.
  - `LiveRecordings.FailOrphansAsync(AnamnysDbContext db, DateTimeOffset now, CancellationToken ct) → Task<int>`.

- [ ] **Step 1: Write the failing tests** — `LiveSession/LiveRecordingsTests.cs` (real Postgres, same style as `AppointmentNotificationsTests`):

```csharp
using Anamnys.Server.LiveSession;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.LiveSession;

[Collection(SharedAppHostCollection.Name)]
public class LiveRecordingsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CheckBegin_OwnPatient_IsOk()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        (await LiveRecordings.CheckBeginAsync(db, seed.ProviderId, patient, null, "DAP", "pt", Ct)).Should().Be(BeginCheck.Ok);
    }

    [Fact]
    public async Task CheckBegin_OtherProvidersPatient_IsNotFound()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        (await LiveRecordings.CheckBeginAsync(db, seed.ProviderId, patient, null, "DAP", "pt", Ct)).Should().Be(BeginCheck.NotFound);
    }

    [Fact]
    public async Task CheckBegin_AppointmentOfAnotherPatient_IsNotFound()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var ana = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var bia = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        await seed.AddAppointmentAsync(bia, Now, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var appointment = await db.Appointments.Where(a => a.PatientId == bia).Select(a => a.Id).SingleAsync(Ct);

        (await LiveRecordings.CheckBeginAsync(db, seed.ProviderId, ana, appointment, "DAP", "pt", Ct)).Should().Be(BeginCheck.NotFound);
    }

    [Fact]
    public async Task CheckBegin_UnknownFormat_IsInvalid()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        (await LiveRecordings.CheckBeginAsync(db, seed.ProviderId, patient, null, "XYZ", "pt", Ct)).Should().Be(BeginCheck.Invalid);
    }

    [Fact]
    public async Task Finalize_WithoutAppointment_CreatesAttendedSessionAndProcessingNote()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, null);

        var result = await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct);

        result.Status.Should().Be(FinalizeStatus.Ok);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == result.NoteId, Ct);
        note.Status.Should().Be("Processing");
        note.InputMode.Should().Be("VoiceLive");
        note.Format.Should().Be("SOAP");
        note.RecordingId.Should().Be(recordingId);
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == note.SessionId, Ct);
        session.Attendance.Should().Be("attended");
        session.ScheduledStart.Should().BeNull();
        session.ActualStart.Should().Be(Now.AddMinutes(-30));
        session.ActualEnd.Should().Be(Now);
        queue.PositionOf(note.Id).Should().Be(1);
        buffers.Peek(recordingId, seed.ProviderId).Should().BeNull();
    }

    [Fact]
    public async Task Finalize_WithAppointment_CreatesSessionFromItAndLinksIt()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddMinutes(-30), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.PatientId == patient, Ct);
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, appointment.Id);

        var result = await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct);

        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == result.NoteId, Ct);
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == note.SessionId, Ct);
        session.ScheduledStart.Should().Be(appointment.StartsAt);
        session.ScheduledMinutes.Should().Be(50);
        session.Modality.Should().Be(appointment.Modality);
        (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointment.Id, Ct)).SessionId.Should().Be(session.Id);
    }

    [Fact]
    public async Task Finalize_AppointmentWithSession_ReusesIt()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddMinutes(-30), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.PatientId == patient, Ct);
        var first = Recording(seed.ProviderId, patient, appointment.Id);
        var firstNote = (await LiveRecordings.FinalizeAsync(db, first.Buffers, first.Queue, seed.ProviderId, first.RecordingId, Now, Ct)).NoteId;
        var second = Recording(seed.ProviderId, patient, appointment.Id);

        var secondNote = (await LiveRecordings.FinalizeAsync(db, second.Buffers, second.Queue, seed.ProviderId, second.RecordingId, Now.AddMinutes(5), Ct)).NoteId;

        var sessions = await db.Notes.AsNoTracking().Where(n => n.Id == firstNote || n.Id == secondNote).Select(n => n.SessionId).Distinct().ToListAsync(Ct);
        sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task Finalize_Twice_ReturnsTheSameNote()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, null);

        var first = await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct);
        var second = await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct);

        second.Should().Be(first);
        (await db.Notes.CountAsync(n => n.RecordingId == recordingId, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Finalize_WithoutBufferOrNote_IsExpired()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var buffers = new RecordingBuffers(new FakeTimeProvider(Now));

        var result = await LiveRecordings.FinalizeAsync(db, buffers, new TranscriptionQueue(), seed.ProviderId, Guid.NewGuid(), Now, Ct);

        result.Should().Be(new FinalizeResult(FinalizeStatus.Expired, null));
    }

    [Fact]
    public async Task FailOrphans_ProcessingLiveNote_BecomesFailedServerRestart()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (buffers, queue, recordingId) = Recording(seed.ProviderId, patient, null);
        var noteId = (await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, recordingId, Now, Ct)).NoteId;

        await LiveRecordings.FailOrphansAsync(db, Now.AddMinutes(1), Ct);

        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.Status.Should().Be("Failed");
        note.FailureReason.Should().Be("server_restart");
    }

    private static (RecordingBuffers Buffers, TranscriptionQueue Queue, Guid RecordingId) Recording(Guid provider, Guid patient, Guid? appointment)
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Now));
        var id = Guid.NewGuid();
        buffers.Begin(id, new RecordingMeta(provider, patient, appointment, "SOAP", "pt", Now.AddMinutes(-30)));
        buffers.Append(id, provider, 0, [1, 0, 2, 0]);
        return (buffers, new TranscriptionQueue(), id);
    }
}
```

- [ ] **Step 2: Run to verify failure.**
Run: `dotnet run --project anamnys-aspire.Tests -- -class "*LiveRecordingsTests*"`
Expected: build FAIL — `LiveRecordings` / `TranscriptionQueue` not found.

- [ ] **Step 3: Implement `TranscriptionQueue.cs`.**

```csharp
using System.Threading.Channels;

namespace Anamnys.Server.LiveSession;

public sealed record TranscriptionJob(Guid NoteId, Guid ProviderId, string Language, PcmAudio Audio);

// In-memory only: a restart loses queued audio on purpose (spec §2). Positions are 1-based.
public sealed class TranscriptionQueue
{
    private readonly Channel<TranscriptionJob> _channel = Channel.CreateUnbounded<TranscriptionJob>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly List<Guid> _pending = [];
    private readonly Lock _lock = new();

    public ChannelReader<TranscriptionJob> Reader => _channel.Reader;

    public void Enqueue(TranscriptionJob job)
    {
        lock (_lock)
        {
            _pending.Add(job.NoteId);
        }
        _channel.Writer.TryWrite(job);
    }

    public void MarkStarted(Guid noteId)
    {
        lock (_lock)
        {
            _pending.Remove(noteId);
        }
    }

    public int PositionOf(Guid noteId)
    {
        lock (_lock)
        {
            return _pending.IndexOf(noteId) + 1;
        }
    }
}
```

- [ ] **Step 4: Implement `LiveRecordings.cs`.**

```csharp
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.LiveSession;

public enum BeginCheck { Ok, Invalid, NotFound }

public enum FinalizeStatus { Ok, Expired }

public sealed record FinalizeResult(FinalizeStatus Status, Guid? NoteId);

// Database side of a live recording (design/specs/2026-10-10-live-session-transcription-design.md §5).
public static class LiveRecordings
{
    public static async Task<BeginCheck> CheckBeginAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid? appointmentId, string format, string language, CancellationToken ct)
    {
        if (!LiveSessionRules.IsValid(format, language))
        {
            return BeginCheck.Invalid;
        }
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.ProviderId == providerId, ct))
        {
            return BeginCheck.NotFound;
        }
        if (appointmentId is { } id
            && !await db.Appointments.AnyAsync(a => a.Id == id && a.ProviderId == providerId && a.PatientId == patientId, ct))
        {
            return BeginCheck.NotFound;
        }
        return BeginCheck.Ok;
    }

    public static async Task<FinalizeResult> FinalizeAsync(
        AnamnysDbContext db, RecordingBuffers buffers, TranscriptionQueue queue,
        Guid providerId, Guid recordingId, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.Notes.AsNoTracking()
            .Where(n => n.RecordingId == recordingId && n.ProviderId == providerId)
            .Select(n => (Guid?)n.Id)
            .SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            return new FinalizeResult(FinalizeStatus.Ok, existing);
        }
        if (buffers.Take(recordingId, providerId) is not { } taken)
        {
            return new FinalizeResult(FinalizeStatus.Expired, null);
        }

        try
        {
            var meta = taken.Meta;
            var noteId = Guid.NewGuid();
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var session = await SessionForAsync(db, meta, now, ct);
                db.Notes.Add(new Note
                {
                    Id = noteId,
                    ProviderId = providerId,
                    PatientId = meta.PatientId,
                    SessionId = session.Id,
                    Status = "Processing",
                    InputMode = "VoiceLive",
                    Format = meta.Format,
                    RecordingId = recordingId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
            queue.Enqueue(new TranscriptionJob(noteId, providerId, meta.Language, taken.Audio));
            return new FinalizeResult(FinalizeStatus.Ok, noteId);
        }
        catch
        {
            taken.Audio.Destroy();
            throw;
        }
    }

    public static Task<int> FailOrphansAsync(AnamnysDbContext db, DateTimeOffset now, CancellationToken ct) =>
        db.Notes
            .Where(n => n.Status == "Processing" && n.RecordingId != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, "Failed")
                .SetProperty(n => n.FailureReason, "server_restart")
                .SetProperty(n => n.UpdatedAt, now), ct);

    private static async Task<Session> SessionForAsync(AnamnysDbContext db, RecordingMeta meta, DateTimeOffset now, CancellationToken ct)
    {
        var appointment = meta.AppointmentId is { } id
            ? await db.Appointments.SingleAsync(a => a.Id == id && a.ProviderId == meta.ProviderId, ct)
            : null;
        var session = appointment?.SessionId is { } sessionId
            ? await db.Sessions.SingleAsync(s => s.Id == sessionId, ct)
            : null;
        if (session is null)
        {
            session = new Session
            {
                Id = Guid.NewGuid(),
                PatientId = meta.PatientId,
                ProviderId = meta.ProviderId,
                ScheduledStart = appointment?.StartsAt,
                ScheduledMinutes = appointment is null ? null : (int)(appointment.EndsAt - appointment.StartsAt).TotalMinutes,
                Modality = appointment?.Modality,
                ActualStart = meta.StartedAt,
                CreatedAt = now,
            };
            db.Sessions.Add(session);
            if (appointment is not null)
            {
                appointment.SessionId = session.Id;
            }
        }
        session.ActualStart ??= meta.StartedAt;
        session.ActualEnd = now;
        session.Attendance = "attended";
        return session;
    }
}
```

`Appointments.Modality` values must fit `Sessions_Modality_ck` (`presencial`, `online`); check the appointment check constraint in `anamnys-db-script.sql` — if appointments allow other values, map anything else to `null`.

- [ ] **Step 5: Run tests to verify they pass.** Same command. Expected: PASS (10 tests).

- [ ] **Step 6: Stage for review.**

---

### Task 4: Transcriber contract and the transcription job

**Files:**
- Create: `anamnys-aspire.Server/LiveSession/ITranscriber.cs`, `LiveSession/NoteTranscription.cs`
- Test: `anamnys-aspire.Tests/LiveSession/FakeTranscriber.cs`, `LiveSession/NoteTranscriptionTests.cs`

**Interfaces:**
- Consumes: `TranscriptionJob`, `PcmAudio` (Tasks 2–3), `Note` (Task 1).
- Produces:
  - `interface ITranscriber { Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct); }`
  - `enum TranscriptionOutcome { Ready, NoSpeech, Error }`
  - `NoteTranscription.ProcessAsync(AnamnysDbContext db, ITranscriber transcriber, TranscriptionJob job, TimeProvider clock, ILogger logger, CancellationToken ct) → Task<TranscriptionOutcome>`

- [ ] **Step 1: Write the fake and the failing tests.**

`FakeTranscriber.cs`:

```csharp
using Anamnys.Server.LiveSession;

namespace Anamnys.Tests.LiveSession;

internal sealed class FakeTranscriber(Func<float[], string> transcribe) : ITranscriber
{
    public int Calls { get; private set; }

    public Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(transcribe(samples));
    }
}
```

`NoteTranscriptionTests.cs`:

```csharp
using Anamnys.Server.LiveSession;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.LiveSession;

[Collection(SharedAppHostCollection.Name)]
public class NoteTranscriptionTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Process_WithText_WritesTranscriptReadyAndDestroysAudio()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var (db, job, bytes) = await QueuedJobAsync(seed);
        await using var _ = db;

        var outcome = await NoteTranscription.ProcessAsync(db, new FakeTranscriber(_ => "  Paciente relata melhora.  "), job,
            new FakeTimeProvider(Now.AddMinutes(2)), NullLogger.Instance, Ct);

        outcome.Should().Be(TranscriptionOutcome.Ready);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == job.NoteId, Ct);
        note.Status.Should().Be("ReadyForReview");
        note.RawTranscript.Should().Be("Paciente relata melhora.");
        note.UpdatedAt.Should().Be(Now.AddMinutes(2));
        bytes.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public async Task Process_WithBlankText_FailsNoSpeechAndDestroysAudio()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var (db, job, bytes) = await QueuedJobAsync(seed);
        await using var _ = db;

        var outcome = await NoteTranscription.ProcessAsync(db, new FakeTranscriber(_ => " \n "), job,
            new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        outcome.Should().Be(TranscriptionOutcome.NoSpeech);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == job.NoteId, Ct);
        note.Status.Should().Be("Failed");
        note.FailureReason.Should().Be("no_speech");
        note.RawTranscript.Should().BeNull();
        bytes.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public async Task Process_WhenTranscriberThrows_FailsTranscriptionErrorAndDestroysAudio()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var (db, job, bytes) = await QueuedJobAsync(seed);
        await using var _ = db;

        var outcome = await NoteTranscription.ProcessAsync(db, new FakeTranscriber(_ => throw new InvalidOperationException("boom")), job,
            new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        outcome.Should().Be(TranscriptionOutcome.Error);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == job.NoteId, Ct);
        note.Status.Should().Be("Failed");
        note.FailureReason.Should().Be("transcription_error");
        bytes.Should().OnlyContain(b => b == 0);
    }

    // Finalizes a real recording so the note and session exist as in production; returns the
    // job's backing bytes so the test can check they were zeroed.
    private static async Task<(Server.Data.AnamnysDbContext Db, TranscriptionJob Job, byte[] Bytes)> QueuedJobAsync(PatientSearchSeed seed)
    {
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var db = seed.CreateDbContext();
        var buffers = new RecordingBuffers(new FakeTimeProvider(Now));
        var queue = new TranscriptionQueue();
        var id = Guid.NewGuid();
        buffers.Begin(id, new RecordingMeta(seed.ProviderId, patient, null, "DAP", "pt", Now.AddMinutes(-10)));
        buffers.Append(id, seed.ProviderId, 0, [1, 0, 2, 0]);
        await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, id, Now, Ct);
        queue.Reader.TryRead(out var queued).Should().BeTrue();
        var bytes = new byte[] { 1, 0, 2, 0 };
        queued!.Audio.Destroy();
        return (db, queued with { Audio = new PcmAudio(bytes, bytes.Length) }, bytes);
    }
}
```

If `Server.Data.AnamnysDbContext` does not resolve from the test namespace, add `using Anamnys.Server.Data;` and use `AnamnysDbContext`.

- [ ] **Step 2: Run to verify failure.**
Run: `dotnet run --project anamnys-aspire.Tests -- -class "*NoteTranscriptionTests*"`
Expected: build FAIL — `ITranscriber` / `NoteTranscription` not found.

- [ ] **Step 3: Implement.**

`ITranscriber.cs`:

```csharp
namespace Anamnys.Server.LiveSession;

// PCM samples in [-1, 1] at 16 kHz mono → plain text. In-process only (CLAUDE.md, HIPAA constraints).
public interface ITranscriber
{
    Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct);
}
```

`NoteTranscription.cs`:

```csharp
using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.LiveSession;

public enum TranscriptionOutcome { Ready, NoSpeech, Error }

// One queued recording: transcribe, write the note, and destroy the audio on every path
// (design/specs/2026-10-10-live-session-transcription-design.md §4.3 and §7).
// Delivery 2 adds the structuring step between the transcript and ReadyForReview.
public static class NoteTranscription
{
    public static async Task<TranscriptionOutcome> ProcessAsync(
        AnamnysDbContext db, ITranscriber transcriber, TranscriptionJob job, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        string text;
        try
        {
            text = (await transcriber.TranscribeAsync(job.Audio.ToSamples(), job.Language, ct)).Trim();
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Exception type only: messages from native code could echo input.
            logger.LogError("Transcription failed for note {NoteId}: {ExceptionType}", job.NoteId, e.GetType().Name);
            await FailAsync(db, job.NoteId, "transcription_error", clock.GetUtcNow(), ct);
            return TranscriptionOutcome.Error;
        }
        finally
        {
            job.Audio.Destroy();
        }

        if (text.Length == 0)
        {
            await FailAsync(db, job.NoteId, "no_speech", clock.GetUtcNow(), ct);
            return TranscriptionOutcome.NoSpeech;
        }
        await db.Notes
            .Where(n => n.Id == job.NoteId && n.ProviderId == job.ProviderId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.RawTranscript, text)
                .SetProperty(n => n.Status, "ReadyForReview")
                .SetProperty(n => n.UpdatedAt, clock.GetUtcNow()), ct);
        return TranscriptionOutcome.Ready;
    }

    private static Task<int> FailAsync(AnamnysDbContext db, Guid noteId, string reason, DateTimeOffset now, CancellationToken ct) =>
        db.Notes
            .Where(n => n.Id == noteId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, "Failed")
                .SetProperty(n => n.FailureReason, reason)
                .SetProperty(n => n.UpdatedAt, now), ct);
}
```

- [ ] **Step 4: Run tests to verify they pass.** Same command. Expected: PASS (3 tests).

- [ ] **Step 5: Stage for review.**

---

### Task 5: Whisper.net transcriber and model download

**Files:**
- Modify: `anamnys-aspire.Server/anamnys-aspire.Server.csproj`, `appsettings.json`, `appsettings.Development.json`
- Create: `anamnys-aspire.Server/LiveSession/WhisperModelProvider.cs`, `LiveSession/WhisperTranscriber.cs`, `LiveSession/LiveSessionOptions.cs`
- Test: `anamnys-aspire.Tests/LiveSession/WhisperTranscriberTests.cs`

**Interfaces:**
- Consumes: `ITranscriber` (Task 4).
- Produces:
  - `LiveSessionOptions { string Model; string ModelSha256; string ModelDirectory; Uri ModelBaseUrl }` bound from section `LiveSession`.
  - `WhisperModelProvider(IOptions<LiveSessionOptions> options, IHttpClientFactory http, ILogger<WhisperModelProvider> logger)`: `Task<WhisperFactory> GetFactoryAsync(CancellationToken ct)`; static `Task<bool> VerifyAsync(string path, string expectedSha256, CancellationToken ct)`.
  - `WhisperTranscriber(WhisperModelProvider models) : ITranscriber`.

- [ ] **Step 1: Add packages.** From `anamnys-aspire/`: `dotnet add anamnys-aspire.Server package Whisper.net` and `dotnet add anamnys-aspire.Server package Whisper.net.Runtime`. Read the installed version's README (`~/.nuget/packages/whisper.net/<version>/README.md`) to confirm `WhisperFactory.FromPath`, `CreateBuilder().WithLanguage(...)`, and `ProcessAsync(float[] samples, CancellationToken)` exist with those signatures; adjust Step 4 if they differ.

- [ ] **Step 2: Configuration.** `appsettings.json`:

```json
  "LiveSession": {
    "Model": "medium",
    "ModelSha256": "",
    "ModelDirectory": "",
    "ModelBaseUrl": "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/"
  }
```

`appsettings.Development.json`: `"LiveSession": { "Model": "base", "ModelSha256": "<pin in Step 6>" }`.

An empty `ModelDirectory` resolves to `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "anamnys", "whisper")`. An empty `ModelSha256` refuses to load the model (fail closed: an unpinned download is never trusted).

- [ ] **Step 3: Write the failing tests** — `WhisperTranscriberTests.cs`:

```csharp
using Anamnys.Server.LiveSession;
using FluentAssertions;

namespace Anamnys.Tests.LiveSession;

public class WhisperTranscriberTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Verify_MatchingHash_IsTrueAndMismatch_IsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(path, "abc", Ct);
        try
        {
            const string abcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
            (await WhisperModelProvider.VerifyAsync(path, abcSha256, Ct)).Should().BeTrue();
            (await WhisperModelProvider.VerifyAsync(path, abcSha256.ToUpperInvariant(), Ct)).Should().BeTrue();
            (await WhisperModelProvider.VerifyAsync(path, new string('0', 64), Ct)).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Runs only when a model is already on disk; set ANAMNYS_WHISPER_MODEL to its path.
    // Silence must produce no speech (Whisper may emit "[BLANK_AUDIO]"-style tokens; the
    // transcriber strips bracketed non-speech markers).
    [Fact]
    public async Task Transcribe_TwoSecondsOfSilence_ReturnsEmpty()
    {
        var model = Environment.GetEnvironmentVariable("ANAMNYS_WHISPER_MODEL");
        Assert.SkipWhen(model is null, "ANAMNYS_WHISPER_MODEL is not set.");
        using var factory = Whisper.net.WhisperFactory.FromPath(model!);
        var transcriber = new WhisperTranscriber(_ => Task.FromResult(factory));

        var text = await transcriber.TranscribeAsync(new float[32000], "pt", Ct);

        text.Trim().Should().BeEmpty();
    }
}
```

`WhisperTranscriber` takes a factory delegate so the test can bypass the download; DI passes `models.GetFactoryAsync`.

- [ ] **Step 4: Implement.**

`LiveSessionOptions.cs`:

```csharp
namespace Anamnys.Server.LiveSession;

public sealed class LiveSessionOptions
{
    public string Model { get; set; } = "medium";
    public string ModelSha256 { get; set; } = "";
    public string ModelDirectory { get; set; } = "";
    public Uri ModelBaseUrl { get; set; } = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/");

    public string ResolvedDirectory => ModelDirectory is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "anamnys", "whisper");
}
```

`WhisperModelProvider.cs`:

```csharp
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Whisper.net;

namespace Anamnys.Server.LiveSession;

// Downloads the public ggml model on first use, verifies the pinned SHA-256 and keeps one
// WhisperFactory for the process (spec §4.4). Only the model comes in; nothing goes out.
public sealed class WhisperModelProvider(
    IOptions<LiveSessionOptions> options, IHttpClientFactory http, ILogger<WhisperModelProvider> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;

    public async Task<WhisperFactory> GetFactoryAsync(CancellationToken ct)
    {
        if (_factory is not null)
        {
            return _factory;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (_factory is not null)
            {
                return _factory;
            }
            var o = options.Value;
            if (o.ModelSha256 is not { Length: 64 })
            {
                throw new InvalidOperationException("LiveSession:ModelSha256 must be set to the model's SHA-256.");
            }
            Directory.CreateDirectory(o.ResolvedDirectory);
            var path = Path.Combine(o.ResolvedDirectory, $"ggml-{o.Model}.bin");
            if (!File.Exists(path) || !await VerifyAsync(path, o.ModelSha256, ct))
            {
                await DownloadAsync(new Uri(o.ModelBaseUrl, $"ggml-{o.Model}.bin"), path, o.ModelSha256, ct);
            }
            _factory = WhisperFactory.FromPath(path);
            return _factory;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task<bool> VerifyAsync(string path, string expectedSha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        return string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadAsync(Uri url, string path, string sha256, CancellationToken ct)
    {
        logger.LogInformation("Downloading Whisper model {Url}.", url);
        var temp = path + ".download";
        using (var client = http.CreateClient(nameof(WhisperModelProvider)))
        await using (var source = await client.GetStreamAsync(url, ct))
        await using (var target = File.Create(temp))
        {
            await source.CopyToAsync(target, ct);
        }
        if (!await VerifyAsync(temp, sha256, ct))
        {
            File.Delete(temp);
            throw new InvalidOperationException("Downloaded Whisper model does not match LiveSession:ModelSha256.");
        }
        File.Move(temp, path, overwrite: true);
    }

    public void Dispose() => _factory?.Dispose();
}
```

The `nameof(WhisperModelProvider)` client needs a long timeout: register it in Task 6 with `client.Timeout = TimeSpan.FromMinutes(30)` and **without** the standard resilience handler's 30 s total timeout (`AddServiceDefaults` adds resilience to all clients — check `Extensions.cs`; if it applies globally, remove it for this client with `.RemoveAllResilienceHandlers()` or give it its own `AddStandardResilienceHandler` with `TotalRequestTimeout` 30 minutes).

`WhisperTranscriber.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;

namespace Anamnys.Server.LiveSession;

public sealed partial class WhisperTranscriber(Func<CancellationToken, Task<WhisperFactory>> factory) : ITranscriber
{
    public WhisperTranscriber(WhisperModelProvider models) : this(models.GetFactoryAsync) { }

    public async Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct)
    {
        var whisper = await factory(ct);
        await using var processor = whisper.CreateBuilder().WithLanguage(language).Build();
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            text.Append(segment.Text);
        }
        return NonSpeech().Replace(text.ToString(), "").Trim();
    }

    // Whisper's markers for silence or noise, e.g. "[BLANK_AUDIO]", "(música)".
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex NonSpeech();
}
```

- [ ] **Step 5: Run tests.**
Run: `dotnet run --project anamnys-aspire.Tests -- -class "*WhisperTranscriberTests*"`
Expected: `Verify_…` PASS; `Transcribe_…` SKIPPED.

- [ ] **Step 6: Pin the dev model hash and run the real test once.** Download `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin` to the default directory (`%LOCALAPPDATA%\anamnys\whisper\ggml-base.bin`), compute `certutil -hashfile <path> SHA256` (or `sha256sum`), and cross-check it against the SHA-256 Hugging Face shows on the file's page. Put it in `appsettings.Development.json`. Then run with `ANAMNYS_WHISPER_MODEL=<path>` set. Expected: PASS. Do the same for the production model (`medium`) and record its hash in `appsettings.json` only after cross-checking it on Hugging Face.

- [ ] **Step 7: Stage for review.**

---

### Task 6: Hubs, worker, sweepers and wiring

**Files:**
- Create: `anamnys-aspire.Server/LiveSession/LiveSessionContracts.cs`, `TranscriptionHub.cs`, `ProgressHub.cs`, `TranscriptionWorker.cs`, `RecordingBufferSweeper.cs`, `OrphanNoteSweep.cs`, `LiveSessionSetup.cs`
- Modify: `anamnys-aspire.Server/Program.cs`, `anamnys-aspire.Tests/anamnys-aspire.Tests.csproj`
- Test: `anamnys-aspire.Tests/LiveSession/TranscriptionHubTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–5.
- Produces (hub contract used by the frontend in Task 8):
  - `/hubs/transcription`: `BeginRecording(Guid recordingId, Guid patientId, Guid? appointmentId, string format, string language) → BeginAck { string Status }` with status `ok` | `invalid` | `not_found` | `forbidden`; `SendAudioChunk(Guid recordingId, int seq, byte[] chunk) → ChunkAck { string Status, int LastSeq }` with status `accepted` | `duplicate` | `gap` | `buffer_expired` | `forbidden` | `too_long`; `FinalizeStream(Guid recordingId) → FinalizeAck { string Status, Guid? NoteId }` with status `ok` | `buffer_expired`.
  - `/hubs/progress`: `SubscribeToNote(Guid noteId) → NoteSnapshot { string Status, int QueuePosition, string? FailureReason }` or `null` when not found; server events `NoteProgress(Guid noteId, string stage, int queuePosition)`, `NoteReady(Guid noteId)`, `NoteFailed(Guid noteId, string reason)`. Group name: `note:{noteId}`.
  - `LiveSessionSetup.AddLiveSession(this WebApplicationBuilder)`, `LiveSessionSetup.MapLiveSessionHubs(this WebApplication)`.

- [ ] **Step 1: Test package.** `dotnet add anamnys-aspire.Tests package Microsoft.AspNetCore.SignalR.Client` (same major version as the server's ASP.NET Core, 10.x).

- [ ] **Step 2: Write the failing hub tests** — `TranscriptionHubTests.cs`. They go through the provider dev server (`http://localhost:5273`), whose Vite proxy forwards `/hubs` with WebSockets, exactly like the browser:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;

namespace Anamnys.Tests.LiveSession;

[Collection(SharedAppHostCollection.Name)]
public class TranscriptionHubTests(SharedAppHostFixture fixture)
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/hubs/transcription/negotiate?negotiateVersion=1")]
    [InlineData("/hubs/progress/negotiate?negotiateVersion=1")]
    public async Task Negotiate_WithoutSession_Returns401(string path)
    {
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        using var response = await client.PostAsync(path, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FullRecording_BeginChunksFinalize_ReturnsTheSameNoteTwice()
    {
        using var session = await LoginAsProviderAsync();
        var providerId = await ProviderIdAsync(session);
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Hub", "Teste", providerId: providerId, cancellationToken: Ct);
        await using var hub = await ConnectAsync(session, "/hubs/transcription");
        var recordingId = Guid.NewGuid();

        var begin = await hub.InvokeAsync<JsonElement>("BeginRecording", recordingId, patient, (Guid?)null, "DAP", "pt", Ct);
        var first = await hub.InvokeAsync<JsonElement>("SendAudioChunk", recordingId, 0, new byte[3200], Ct);
        var again = await hub.InvokeAsync<JsonElement>("SendAudioChunk", recordingId, 0, new byte[3200], Ct);
        var gap = await hub.InvokeAsync<JsonElement>("SendAudioChunk", recordingId, 5, new byte[3200], Ct);
        var finalized = await hub.InvokeAsync<JsonElement>("FinalizeStream", recordingId, Ct);
        var repeated = await hub.InvokeAsync<JsonElement>("FinalizeStream", recordingId, Ct);

        begin.GetProperty("status").GetString().Should().Be("ok");
        first.GetProperty("status").GetString().Should().Be("accepted");
        again.GetProperty("status").GetString().Should().Be("duplicate");
        gap.GetProperty("status").GetString().Should().Be("gap");
        gap.GetProperty("lastSeq").GetInt32().Should().Be(0);
        finalized.GetProperty("status").GetString().Should().Be("ok");
        repeated.GetProperty("noteId").GetGuid().Should().Be(finalized.GetProperty("noteId").GetGuid());
    }

    [Fact]
    public async Task BeginRecording_OtherProvidersPatient_IsNotFound()
    {
        using var session = await LoginAsProviderAsync();
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var stranger = await seed.AddPatientAsync("Outro", "Paciente", cancellationToken: Ct);
        await using var hub = await ConnectAsync(session, "/hubs/transcription");

        var begin = await hub.InvokeAsync<JsonElement>("BeginRecording", Guid.NewGuid(), stranger, (Guid?)null, "DAP", "pt", Ct);

        begin.GetProperty("status").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task SubscribeToNote_OtherProvidersNote_ReturnsNull()
    {
        using var session = await LoginAsProviderAsync();
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var stranger = await seed.AddPatientAsync("Outro", "Paciente", cancellationToken: Ct);
        await seed.AddNoteAsync(stranger, "Draft", DateTimeOffset.UtcNow, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var noteId = db.Notes.Single(n => n.PatientId == stranger).Id;
        await using var hub = await ConnectAsync(session, "/hubs/progress");

        var snapshot = await hub.InvokeAsync<JsonElement?>("SubscribeToNote", noteId, Ct);

        (snapshot is null || snapshot.Value.ValueKind == JsonValueKind.Null).Should().BeTrue();
    }

    private static async Task<HubConnection> ConnectAsync(BrowserSession session, string path)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(ProviderBaseAddress, path), o =>
                o.Headers["Cookie"] = string.Join("; ", session.Jar.Select(kv => $"{kv.Key}={kv.Value}")))
            .Build();
        await connection.StartAsync(Ct);
        return connection;
    }

    private static async Task<Guid> ProviderIdAsync(BrowserSession session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/phi/probe");
        using var response = await session.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("localId").GetGuid();
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
```

The happy-path test leaves a note for the dev provider that the worker will try to transcribe (3200 bytes of silence). With no model hash configured in the test run, it ends `Failed`; that is fine. If the dev environment has a model hash set, it ends `Failed` (`no_speech`).

- [ ] **Step 3: Run to verify failure.**
Run: `dotnet run --project anamnys-aspire.Tests -- -class "*TranscriptionHubTests*"`
Expected: FAIL — negotiate returns 404 (no hub mapped).

- [ ] **Step 4: Contracts** — `LiveSessionContracts.cs`:

```csharp
namespace Anamnys.Server.LiveSession;

public sealed record BeginAck(string Status);

public sealed record ChunkAck(string Status, int LastSeq);

public sealed record FinalizeAck(string Status, Guid? NoteId);

public sealed record NoteSnapshot(string Status, int QueuePosition, string? FailureReason);
```

- [ ] **Step 5: `TranscriptionHub.cs`.**

```csharp
using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Anamnys.Server.LiveSession;

// design/specs/2026-10-10-live-session-transcription-design.md §4.1. Providers cookie only: any
// other realm is not an authenticated principal here (401), as on /api/phi.
[Authorize(AuthenticationSchemes = AuthSchemes.ProviderCookie)]
public sealed class TranscriptionHub(
    RecordingBuffers buffers, TranscriptionQueue queue, AnamnysDbContext db, TimeProvider clock) : Hub
{
    private Guid ProviderId => Context.User!.LocalId();

    public async Task<BeginAck> BeginRecording(Guid recordingId, Guid patientId, Guid? appointmentId, string format, string language)
    {
        var check = await LiveRecordings.CheckBeginAsync(db, ProviderId, patientId, appointmentId, format, language, Context.ConnectionAborted);
        if (check != BeginCheck.Ok)
        {
            return new BeginAck(check == BeginCheck.Invalid ? "invalid" : "not_found");
        }
        var meta = new RecordingMeta(ProviderId, patientId, appointmentId, format, language, clock.GetUtcNow());
        return new BeginAck(buffers.Begin(recordingId, meta) == BeginStatus.Forbidden ? "forbidden" : "ok");
    }

    public ChunkAck SendAudioChunk(Guid recordingId, int seq, byte[] chunk)
    {
        var result = buffers.Append(recordingId, ProviderId, seq, chunk);
        Array.Clear(chunk);
        return new ChunkAck(result.Status switch
        {
            ChunkStatus.Accepted => "accepted",
            ChunkStatus.Duplicate => "duplicate",
            ChunkStatus.Gap => "gap",
            ChunkStatus.Expired => "buffer_expired",
            ChunkStatus.TooLong => "too_long",
            _ => "forbidden",
        }, result.LastSeq);
    }

    public async Task<FinalizeAck> FinalizeStream(Guid recordingId)
    {
        var result = await LiveRecordings.FinalizeAsync(db, buffers, queue, ProviderId, recordingId, clock.GetUtcNow(), Context.ConnectionAborted);
        return result.Status == FinalizeStatus.Ok
            ? new FinalizeAck("ok", result.NoteId)
            : new FinalizeAck("buffer_expired", null);
    }
}
```

- [ ] **Step 6: `ProgressHub.cs`.**

```csharp
using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.LiveSession;

[Authorize(AuthenticationSchemes = AuthSchemes.ProviderCookie)]
public sealed class ProgressHub(AnamnysDbContext db, TranscriptionQueue queue) : Hub
{
    public static string Group(Guid noteId) => $"note:{noteId}";

    public async Task<NoteSnapshot?> SubscribeToNote(Guid noteId)
    {
        var providerId = Context.User!.LocalId();
        var note = await db.Notes.AsNoTracking()
            .Where(n => n.Id == noteId && n.ProviderId == providerId)
            .Select(n => new { n.Status, n.FailureReason })
            .SingleOrDefaultAsync(Context.ConnectionAborted);
        if (note is null)
        {
            return null;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(noteId), Context.ConnectionAborted);
        return new NoteSnapshot(note.Status, queue.PositionOf(noteId), note.FailureReason);
    }
}
```

- [ ] **Step 7: Hosted services.**

`TranscriptionWorker.cs`:

```csharp
using Anamnys.Server.Data;
using Microsoft.AspNetCore.SignalR;

namespace Anamnys.Server.LiveSession;

// One job at a time: Whisper is CPU-bound (spec §4.3).
public sealed class TranscriptionWorker(
    TranscriptionQueue queue, IServiceScopeFactory scopes, IHubContext<ProgressHub> progress,
    TimeProvider clock, ILogger<TranscriptionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            queue.MarkStarted(job.NoteId);
            var group = progress.Clients.Group(ProgressHub.Group(job.NoteId));
            try
            {
                await group.SendAsync("NoteProgress", job.NoteId, "transcribing", 0, stoppingToken);
                using var scope = scopes.CreateScope();
                var outcome = await NoteTranscription.ProcessAsync(
                    scope.ServiceProvider.GetRequiredService<AnamnysDbContext>(),
                    scope.ServiceProvider.GetRequiredService<ITranscriber>(),
                    job, clock, logger, stoppingToken);
                await (outcome switch
                {
                    TranscriptionOutcome.Ready => group.SendAsync("NoteReady", job.NoteId, stoppingToken),
                    TranscriptionOutcome.NoSpeech => group.SendAsync("NoteFailed", job.NoteId, "no_speech", stoppingToken),
                    _ => group.SendAsync("NoteFailed", job.NoteId, "transcription_error", stoppingToken),
                });
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Transcription job for note {NoteId} failed: {ExceptionType}", job.NoteId, e.GetType().Name);
            }
            finally
            {
                job.Audio.Destroy();
            }
        }
    }
}
```

`RecordingBufferSweeper.cs`:

```csharp
namespace Anamnys.Server.LiveSession;

public sealed class RecordingBufferSweeper(RecordingBuffers buffers, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            buffers.SweepExpired();
        }
    }
}
```

`OrphanNoteSweep.cs` (registered after `DatabaseInitializer`, so the schema exists):

```csharp
using Anamnys.Server.Data;

namespace Anamnys.Server.LiveSession;

// Processing live notes left by a previous process lost their audio with it (spec §4.6).
public sealed class OrphanNoteSweep(IServiceScopeFactory scopes, TimeProvider clock, ILogger<OrphanNoteSweep> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
        var failed = await LiveRecordings.FailOrphansAsync(db, clock.GetUtcNow(), cancellationToken);
        if (failed > 0)
        {
            logger.LogWarning("Marked {Count} orphan live-session notes as failed.", failed);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

- [ ] **Step 8: `LiveSessionSetup.cs` and `Program.cs`.**

```csharp
namespace Anamnys.Server.LiveSession;

public static class LiveSessionSetup
{
    public static void AddLiveSession(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<LiveSessionOptions>(builder.Configuration.GetSection("LiveSession"));
        builder.Services.AddHttpClient(nameof(WhisperModelProvider), c => c.Timeout = TimeSpan.FromMinutes(30));
        builder.Services.AddSingleton<WhisperModelProvider>();
        builder.Services.AddSingleton<ITranscriber, WhisperTranscriber>();
        builder.Services.AddSingleton<RecordingBuffers>();
        builder.Services.AddSingleton<TranscriptionQueue>();
        builder.Services.AddHostedService<OrphanNoteSweep>();
        builder.Services.AddHostedService<TranscriptionWorker>();
        builder.Services.AddHostedService<RecordingBufferSweeper>();
        builder.Services.AddSignalR();
    }

    public static void MapLiveSessionHubs(this WebApplication app)
    {
        app.MapHub<TranscriptionHub>("/hubs/transcription");
        app.MapHub<ProgressHub>("/hubs/progress");
    }
}
```

`WhisperTranscriber` has two constructors; DI picks the one it can satisfy (`WhisperModelProvider`). If DI reports ambiguity, register with a factory: `AddSingleton<ITranscriber>(sp => new WhisperTranscriber(sp.GetRequiredService<WhisperModelProvider>()))`.

`Program.cs`: `using Anamnys.Server.LiveSession;`; call `builder.AddLiveSession();` right after `builder.Services.AddHostedService<DatabaseInitializer>();` (hosted services start in registration order, so `OrphanNoteSweep` runs after the schema bootstrap); call `app.MapLiveSessionHubs();` next to `app.MapAuthEndpoints();`. Apply the Step 5 note in Task 5 about the resilience handler: check `Extensions.cs` for `ConfigureHttpClientDefaults(... AddStandardResilienceHandler())`; if present, chain `.RemoveAllResilienceHandlers()` onto the `AddHttpClient` call above (package `Microsoft.Extensions.Http.Resilience` is already referenced).

- [ ] **Step 9: Run tests.** `aspire stop` first if running.
Run: `dotnet run --project anamnys-aspire.Tests -- -class "*TranscriptionHubTests*" -class "*LiveRecordingsTests*" -class "*NoteTranscriptionTests*"`
Expected: PASS.

- [ ] **Step 10: Stage for review.**

---

### Task 7: Note detail endpoint

**Files:**
- Create: `anamnys-aspire.Server/Notes/NoteQueries.cs`, `Notes/NoteEndpoints.cs`
- Modify: `anamnys-aspire.Server/Program.cs`, `anamnys-aspire.Server/anamnys-aspire.Server.http`
- Test: `anamnys-aspire.Tests/Notes/NoteEndpointTests.cs`

**Interfaces:**
- Produces: `GET /api/phi/providers/me/notes/{noteId}` → `NoteDetail(Guid Id, Guid PatientId, string PatientName, string Status, string InputMode, string Format, string? RawTranscript, string? FailureReason, DateTimeOffset CreatedAt)`; `NoteQueries.GetAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct) → Task<NoteDetail?>`; `NoteEndpoints.MapNoteEndpoints(this RouteGroupBuilder phi)`.

- [ ] **Step 1: Write the failing tests** — `Notes/NoteEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Server.Notes;
using Anamnys.Tests.Auth;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notes;

[Collection(SharedAppHostCollection.Name)]
public class NoteEndpointTests(SharedAppHostFixture fixture)
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_WithoutSession_Returns401()
    {
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        using var response = await client.GetAsync($"/api/phi/providers/me/notes/{Guid.NewGuid()}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_OtherProvidersNote_Returns404()
    {
        using var session = await LoginAsProviderAsync();
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var stranger = await seed.AddPatientAsync("Outro", "Paciente", cancellationToken: Ct);
        await seed.AddNoteAsync(stranger, "Draft", DateTimeOffset.UtcNow, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var noteId = await db.Notes.Where(n => n.PatientId == stranger).Select(n => n.Id).SingleAsync(Ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/phi/providers/me/notes/{noteId}");
        using var response = await session.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAsync_OwnNote_ReturnsTranscriptAndPatientName()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddNoteAsync(patient, "Draft", DateTimeOffset.UtcNow, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var noteId = await db.Notes.Where(n => n.PatientId == patient).Select(n => n.Id).SingleAsync(Ct);
        await db.Notes.Where(n => n.Id == noteId).ExecuteUpdateAsync(s => s.SetProperty(n => n.RawTranscript, "Texto."), Ct);

        var detail = await NoteQueries.GetAsync(db, seed.ProviderId, noteId, Ct);

        detail.Should().NotBeNull();
        detail!.PatientName.Should().Be("Ana Silva");
        detail.RawTranscript.Should().Be("Texto.");
        detail.InputMode.Should().Be("Text");
        (await NoteQueries.GetAsync(db, Guid.NewGuid(), noteId, Ct)).Should().BeNull();
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
```

Check how other queries build the display name (`grep -rn "FirstName + \" \"" anamnys-aspire.Server`) and match it.

- [ ] **Step 2: Run to verify failure.**
Run: `dotnet run --project anamnys-aspire.Tests -- -class "*NoteEndpointTests*"`
Expected: build FAIL — `Anamnys.Server.Notes` not found.

- [ ] **Step 3: Implement.**

`Notes/NoteQueries.cs`:

```csharp
using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

public sealed record NoteDetail(
    Guid Id, Guid PatientId, string PatientName, string Status, string InputMode, string Format,
    string? RawTranscript, string? FailureReason, DateTimeOffset CreatedAt);

public static class NoteQueries
{
    public static Task<NoteDetail?> GetAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct) =>
        (from n in db.Notes.AsNoTracking()
         join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
         where n.Id == noteId && n.ProviderId == providerId
         select new NoteDetail(n.Id, n.PatientId, p.FirstName + " " + p.LastName, n.Status, n.InputMode, n.Format,
             n.RawTranscript, n.FailureReason, n.CreatedAt))
        .SingleOrDefaultAsync(ct);
}
```

`Notes/NoteEndpoints.cs`:

```csharp
using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Notes;

public static class NoteEndpoints
{
    public static void MapNoteEndpoints(this RouteGroupBuilder phi)
    {
        var notes = phi.MapGroup("providers/me/notes");

        notes.MapGet("{noteId:guid}", async (Guid noteId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var note = await NoteQueries.GetAsync(db, providerId, noteId, ct);
            return note is null ? Results.NotFound() : Results.Ok(note);
        });
    }
}
```

`Program.cs`: `using Anamnys.Server.Notes;` and `phi.MapNoteEndpoints();` after `phi.MapAppointmentEndpoints();`.

`anamnys-aspire.Server.http`: add a section in the file's existing style:

```http
### Note detail (provider). Status, raw transcript and failure reason of one note.
GET {{server}}/api/phi/providers/me/notes/{{noteId}}
Cookie: {{providerCookie}}
```

(Match the variable names the file already uses for the base URL and cookie.)

- [ ] **Step 4: Run tests.** Same command. Expected: PASS (3 tests). Then run the `.http` request against the running AppHost (CLAUDE.md "Creating/Modifying API Endpoints" step 5).

- [ ] **Step 5: Stage for review.**

---

### Task 8: Frontend plumbing — types, API, hubs, worklet, recorder hook

**Files:**
- Modify: `packages/shared/src/lib/types.ts`, `packages/shared/src/api/notes.ts`
- Rewrite: `apps/provider/src/lib/signalr.ts`
- Create: `apps/provider/public/worklets/pcm16-recorder.js`, `apps/provider/src/lib/chunkLog.ts`, `apps/provider/src/hooks/useLiveRecorder.ts`

**Interfaces:**
- Consumes: hub contract from Task 6; `GET /api/phi/providers/me/notes/{id}` from Task 7.
- Produces:
  - Types: `LiveNoteFormat = "DAP" | "SOAP" | "Biopsychosocial"`, `LiveLanguage = "pt" | "en" | "es"`, `NoteDetail`, `NoteSnapshot`.
  - `notesApi.getDetail(noteId): Promise<NoteDetail>`.
  - `liveSessionHub`: `connect(onReconnected: () => void, onReconnecting: () => void, onClose: () => void)`, `disconnect()`, `begin(recordingId, patientId, appointmentId, format, language): Promise<BeginAck>`, `sendChunk(recordingId, seq, bytes: Uint8Array): Promise<ChunkAck>`, `finalize(recordingId): Promise<FinalizeAck>`.
  - `progressHub`: `connect()`, `disconnect()`, `subscribe(noteId): Promise<NoteSnapshot | null>`, `onProgress(cb)`, `onReady(cb)`, `onFailed(cb)`.
  - `useLiveRecorder()` → `{ state: "idle" | "recording" | "paused" | "finalizing" | "done" | "error"; connection: "connected" | "connecting"; elapsedMs; level; error: LiveError | null; noteId: string | null; start(opts), pause(), resume(), stop(), reset() }`.

- [ ] **Step 1: Types** — append to `packages/shared/src/lib/types.ts`:

```ts
// Live session (design/specs/2026-10-10-live-session-transcription-design.md).
export type LiveNoteFormat = "DAP" | "SOAP" | "Biopsychosocial";
export type LiveLanguage = "pt" | "en" | "es";

// Server status strings, as stored in Notes.Status.
export type NoteDetailStatus = "Draft" | "Processing" | "ReadyForReview" | "Signed" | "Exported" | "Failed";
export type NoteFailureReason = "no_speech" | "transcription_error" | "server_restart";

export interface NoteDetail {
  id: string;
  patientId: string;
  patientName: string;
  status: NoteDetailStatus;
  inputMode: string;
  format: string;
  rawTranscript: string | null;
  failureReason: NoteFailureReason | null;
  createdAt: string;
}

export interface NoteSnapshot {
  status: NoteDetailStatus;
  queuePosition: number;
  failureReason: NoteFailureReason | null;
}
```

- [ ] **Step 2: API** — in `packages/shared/src/api/notes.ts` add (keep the legacy methods; `PatientNotesView` still uses `list`):

```ts
  getDetail: async (noteId: string): Promise<NoteDetail> => {
    const { data } = await api.get<NoteDetail>(`/phi/providers/me/notes/${noteId}`);
    return data;
  },
```

and import `NoteDetail` from types. Check `api/client.ts` for its base URL: the appointments module uses `/phi/...`, so the client already prefixes `/api`.

- [ ] **Step 3: Rewrite `apps/provider/src/lib/signalr.ts`** (nothing else imports it — verify with `grep -rn "lib/signalr" apps packages`):

```ts
import * as signalR from "@microsoft/signalr";
import type { LiveLanguage, LiveNoteFormat, NoteFailureReason, NoteSnapshot } from "@anamnys/shared/lib/types";

// Unset in normal Aspire-hosted dev/prod — relative "/hubs" resolves through vite.config.ts's
// same-origin proxy to the server resource (see also api/client.ts).
const SIGNALR_URL = import.meta.env.VITE_SIGNALR_URL ?? "/hubs";

export interface BeginAck { status: "ok" | "invalid" | "not_found" | "forbidden" }
export interface ChunkAck {
  status: "accepted" | "duplicate" | "gap" | "buffer_expired" | "forbidden" | "too_long";
  lastSeq: number;
}
export interface FinalizeAck { status: "ok" | "buffer_expired"; noteId: string | null }

// SignalR's JSON hub protocol serializes byte[] as base64 (System.Text.Json), so the client
// encodes manually.
function toBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.byteLength; i++) binary += String.fromCharCode(bytes[i]);
  return btoa(binary);
}

function build(path: string): signalR.HubConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${SIGNALR_URL}/${path}`, { withCredentials: true })
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 10000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}

let live: signalR.HubConnection | null = null;

export const liveSessionHub = {
  connect: async (onReconnected: () => void, onReconnecting: () => void, onClose: () => void): Promise<void> => {
    live = build("transcription");
    live.onreconnecting(onReconnecting);
    live.onreconnected(onReconnected);
    live.onclose(onClose);
    await live.start();
  },
  disconnect: async (): Promise<void> => {
    await live?.stop();
    live = null;
  },
  isConnected: (): boolean => live?.state === signalR.HubConnectionState.Connected,
  begin: (recordingId: string, patientId: string, appointmentId: string | null, format: LiveNoteFormat, language: LiveLanguage) =>
    live!.invoke<BeginAck>("BeginRecording", recordingId, patientId, appointmentId, format, language),
  sendChunk: (recordingId: string, seq: number, bytes: Uint8Array) =>
    live!.invoke<ChunkAck>("SendAudioChunk", recordingId, seq, toBase64(bytes)),
  finalize: (recordingId: string) => live!.invoke<FinalizeAck>("FinalizeStream", recordingId),
};

let progress: signalR.HubConnection | null = null;

export const progressHub = {
  connect: async (): Promise<void> => {
    progress = build("progress");
    await progress.start();
  },
  disconnect: async (): Promise<void> => {
    await progress?.stop();
    progress = null;
  },
  subscribe: (noteId: string) => progress!.invoke<NoteSnapshot | null>("SubscribeToNote", noteId),
  onProgress: (cb: (noteId: string, stage: string, queuePosition: number) => void) => progress?.on("NoteProgress", cb),
  onReady: (cb: (noteId: string) => void) => progress?.on("NoteReady", cb),
  onFailed: (cb: (noteId: string, reason: NoteFailureReason) => void) => progress?.on("NoteFailed", cb),
};
```

- [ ] **Step 4: Worklet** — `apps/provider/public/worklets/pcm16-recorder.js`. Plain JS (served as-is from `/provider/worklets/`); resamples the context's native rate to 16 kHz by averaging, because Firefox refuses to connect a microphone to an `AudioContext` created at another sample rate:

```js
// Converts microphone audio to PCM 16 kHz mono Int16 LE and posts ~16 KB chunks plus a level.
// design/specs/2026-10-10-live-session-transcription-design.md §6.
const TARGET_RATE = 16000;
const CHUNK_SAMPLES = 8192; // 16 KB of Int16

class Pcm16Recorder extends AudioWorkletProcessor {
  constructor() {
    super();
    this.ratio = sampleRate / TARGET_RATE;
    this.acc = 0;
    this.accCount = 0;
    this.pos = 0;
    this.out = new Int16Array(CHUNK_SAMPLES);
    this.filled = 0;
    this.paused = false;
    this.port.onmessage = (e) => {
      if (e.data === "pause") this.paused = true;
      else if (e.data === "resume") this.paused = false;
      else if (e.data === "flush") this.flush();
    };
  }

  flush() {
    if (this.filled > 0) {
      const chunk = this.out.slice(0, this.filled);
      this.port.postMessage({ type: "chunk", buffer: chunk.buffer }, [chunk.buffer]);
      this.filled = 0;
    }
    this.port.postMessage({ type: "flushed" });
  }

  process(inputs) {
    const channel = inputs[0] && inputs[0][0];
    if (!channel || this.paused) return true;
    let peak = 0;
    for (let i = 0; i < channel.length; i++) {
      const s = channel[i];
      peak = Math.max(peak, Math.abs(s));
      this.acc += s;
      this.accCount++;
      this.pos += 1;
      if (this.pos >= this.ratio) {
        this.pos -= this.ratio;
        const v = Math.max(-1, Math.min(1, this.acc / this.accCount));
        this.out[this.filled++] = v < 0 ? v * 0x8000 : v * 0x7fff;
        this.acc = 0;
        this.accCount = 0;
        if (this.filled === CHUNK_SAMPLES) {
          const chunk = this.out.slice();
          this.port.postMessage({ type: "chunk", buffer: chunk.buffer }, [chunk.buffer]);
          this.filled = 0;
        }
      }
    }
    this.port.postMessage({ type: "level", peak });
    return true;
  }
}

registerProcessor("pcm16-recorder", Pcm16Recorder);
```

Int16Array is little-endian on every platform browsers run on, which is what the server reads.

- [ ] **Step 5: Chunk log** — `apps/provider/src/lib/chunkLog.ts`:

```ts
// Every chunk of a recording, kept in memory until finalize is acknowledged, so a dropped
// connection can resend from the last chunk the server accepted (spec §6).
export class ChunkLog {
  private chunks: Uint8Array[] = [];
  private acked = -1;

  get length(): number { return this.chunks.length; }
  get lastAcked(): number { return this.acked; }

  add(bytes: Uint8Array): number {
    this.chunks.push(bytes);
    return this.chunks.length - 1;
  }

  ack(seq: number): void { this.acked = Math.max(this.acked, seq); }

  // After buffer_expired the server starts from scratch.
  resetAcks(): void { this.acked = -1; }

  pending(): { seq: number; bytes: Uint8Array }[] {
    return this.chunks.slice(this.acked + 1).map((bytes, i) => ({ seq: this.acked + 1 + i, bytes }));
  }

  clear(): void {
    this.chunks.forEach((c) => c.fill(0));
    this.chunks = [];
    this.acked = -1;
  }
}
```

- [ ] **Step 6: Recorder hook** — `apps/provider/src/hooks/useLiveRecorder.ts`:

```ts
import { useCallback, useEffect, useRef, useState } from "react";
import type { LiveLanguage, LiveNoteFormat } from "@anamnys/shared/lib/types";
import { liveSessionHub } from "@/lib/signalr";
import { ChunkLog } from "@/lib/chunkLog";

export type LiveState = "idle" | "recording" | "paused" | "finalizing" | "done" | "error";
export type LiveError = "permission" | "device" | "connect" | "invalid" | "not_found" | "too_long" | "finalize";

export interface StartOptions {
  patientId: string;
  appointmentId: string | null;
  format: LiveNoteFormat;
  language: LiveLanguage;
}

const SAMPLES_PER_MS = 16;
export const WARN_AT_MS = 85 * 60 * 1000;
export const MAX_MS = 90 * 60 * 1000;

// Mic → worklet (PCM 16 kHz) → ChunkLog → TranscriptionHub, resending after reconnects.
export function useLiveRecorder() {
  const [state, setState] = useState<LiveState>("idle");
  const [connection, setConnection] = useState<"connected" | "connecting">("connected");
  const [samples, setSamples] = useState(0);
  const [level, setLevel] = useState(0);
  const [error, setError] = useState<LiveError | null>(null);
  const [noteId, setNoteId] = useState<string | null>(null);

  const log = useRef(new ChunkLog());
  const recordingId = useRef<string>("");
  const opts = useRef<StartOptions | null>(null);
  const audio = useRef<{ ctx: AudioContext; stream: MediaStream; node: AudioWorkletNode } | null>(null);
  const sending = useRef(false);

  const begin = useCallback(async () => {
    const o = opts.current!;
    const ack = await liveSessionHub.begin(recordingId.current, o.patientId, o.appointmentId, o.format, o.language);
    if (ack.status !== "ok") throw Object.assign(new Error(ack.status), { code: ack.status });
  }, []);

  // Sends every pending chunk in order; one loop at a time.
  const pump = useCallback(async () => {
    if (sending.current || !liveSessionHub.isConnected()) return;
    sending.current = true;
    try {
      for (let next = log.current.pending(); next.length > 0; next = log.current.pending()) {
        for (const { seq, bytes } of next) {
          const ack = await liveSessionHub.sendChunk(recordingId.current, seq, bytes);
          if (ack.status === "accepted" || ack.status === "duplicate") log.current.ack(seq);
          else if (ack.status === "gap") { log.current.ack(ack.lastSeq); break; }
          else if (ack.status === "buffer_expired") { log.current.resetAcks(); await begin(); break; }
          else if (ack.status === "too_long") { setError("too_long"); return; }
          else throw new Error(ack.status);
        }
      }
    } catch {
      // Connection trouble: onreconnected calls pump again.
    } finally {
      sending.current = false;
    }
  }, [begin]);

  const releaseAudio = useCallback(() => {
    audio.current?.node.disconnect();
    audio.current?.stream.getTracks().forEach((t) => t.stop());
    void audio.current?.ctx.close();
    audio.current = null;
  }, []);

  const start = useCallback(async (o: StartOptions) => {
    setError(null);
    setNoteId(null);
    setSamples(0);
    log.current.clear();
    recordingId.current = crypto.randomUUID();
    opts.current = o;
    let stream: MediaStream;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
    } catch (e) {
      setError(e instanceof DOMException && e.name === "NotAllowedError" ? "permission" : "device");
      setState("error");
      return;
    }
    try {
      await liveSessionHub.connect(
        () => { setConnection("connected"); void begin().then(pump).catch(() => undefined); },
        () => setConnection("connecting"),
        () => setConnection("connecting"),
      );
      await begin();
    } catch (e) {
      stream.getTracks().forEach((t) => t.stop());
      const code = (e as { code?: string }).code;
      setError(code === "invalid" ? "invalid" : code === "not_found" ? "not_found" : "connect");
      setState("error");
      return;
    }
    const ctx = new AudioContext();
    await ctx.audioWorklet.addModule(`${import.meta.env.BASE_URL}worklets/pcm16-recorder.js`);
    const node = new AudioWorkletNode(ctx, "pcm16-recorder");
    node.port.onmessage = (e: MessageEvent) => {
      if (e.data.type === "chunk") {
        const bytes = new Uint8Array(e.data.buffer as ArrayBuffer);
        log.current.add(bytes);
        setSamples((s) => s + bytes.byteLength / 2);
        void pump();
      } else if (e.data.type === "level") {
        setLevel(e.data.peak as number);
      }
    };
    ctx.createMediaStreamSource(stream).connect(node);
    audio.current = { ctx, stream, node };
    setConnection("connected");
    setState("recording");
  }, [begin, pump]);

  const pause = useCallback(() => { audio.current?.node.port.postMessage("pause"); setState("paused"); }, []);
  const resume = useCallback(() => { audio.current?.node.port.postMessage("resume"); setState("recording"); }, []);

  const stop = useCallback(async () => {
    const node = audio.current?.node;
    if (!node) return;
    setState("finalizing");
    await new Promise<void>((resolve) => {
      const previous = node.port.onmessage;
      node.port.onmessage = (e: MessageEvent) => {
        previous?.call(node.port, e);
        if (e.data.type === "flushed") resolve();
      };
      node.port.postMessage("flush");
    });
    releaseAudio();
    // Keep trying until every chunk is accepted and finalize answers; the audio stays in memory meanwhile.
    for (;;) {
      await pump();
      if (log.current.pending().length === 0 && liveSessionHub.isConnected()) {
        try {
          const ack = await liveSessionHub.finalize(recordingId.current);
          if (ack.status === "ok" && ack.noteId) {
            log.current.clear();
            setNoteId(ack.noteId);
            setState("done");
            await liveSessionHub.disconnect();
            return;
          }
          log.current.resetAcks();
          await begin();
        } catch {
          // fall through and retry
        }
      }
      await new Promise((r) => setTimeout(r, 1000));
    }
  }, [begin, pump, releaseAudio]);

  // "Gravar novamente": back to the prepare step. Only valid once no audio is held.
  const reset = useCallback(() => {
    log.current.clear();
    setError(null);
    setNoteId(null);
    setSamples(0);
    setLevel(0);
    setState("idle");
  }, []);

  const elapsedMs = samples / SAMPLES_PER_MS;
  useEffect(() => {
    if (state === "recording" && elapsedMs >= MAX_MS) void stop();
  }, [state, elapsedMs, stop]);

  useEffect(() => () => { releaseAudio(); log.current.clear(); void liveSessionHub.disconnect(); }, [releaseAudio]);

  return { state, connection, elapsedMs, level, error, noteId, start, pause, resume, stop, reset };
}
```

The finalize loop retries forever while the page is open; the `ProcessingStep` (Task 9) shows "Conectando…" during that time and the navigation block keeps the user from losing the audio by accident.

- [ ] **Step 7: Build and lint.**
Run (from `anamnys-aspire/`): `npm run build` then `npm run lint`
Expected: both succeed. Fix lint findings (e.g. `react-hooks/exhaustive-deps`) without changing behaviour.

- [ ] **Step 8: Stage for review.**

---

### Task 9: `/live-session` screen

**Files:**
- Create: `apps/provider/src/routes/_app/live-session.tsx`, `apps/provider/src/components/live-session/PrepareStep.tsx`, `RecordingStep.tsx`, `ProcessingStep.tsx`
- Modify: `packages/shared/src/lib/i18n/locales/pt.json`, `apps/provider/src/routeTree.gen.ts` (regenerated)

**Interfaces:**
- Consumes: `useLiveRecorder`, `WARN_AT_MS` (Task 8); `progressHub` (Task 8); `PatientPicker` (`apps/provider/src/components/calendar/PatientPicker.tsx`, props `value`, `onChange`, `error`); `useAppointmentsQuery(days, status?)` and `todayIn(PRACTICE_ZONE)`, `formatTime(instant, zone)` from the calendar.

- [ ] **Step 1: i18n** — add to `pt.json` a top-level `"liveSession"` block (keep `nav.liveSession` as is):

```json
  "liveSession": {
    "title": "Sessão ao Vivo",
    "consent": "A transcrição registra o que você dita. Se o paciente estiver audível na gravação, é necessário o consentimento dele.",
    "patient": "Paciente",
    "appointment": "Consulta de hoje (opcional)",
    "noAppointment": "Sem consulta vinculada",
    "format": "Formato da nota",
    "formats": { "DAP": "DAP", "SOAP": "SOAP", "Biopsychosocial": "Biopsicossocial" },
    "language": "Idioma",
    "languages": { "pt": "Português", "en": "Inglês", "es": "Espanhol" },
    "start": "Iniciar gravação",
    "pause": "Pausar",
    "resume": "Retomar",
    "stop": "Parar e gerar nota",
    "paused": "Pausado",
    "connected": "Conectado",
    "connecting": "Reconectando… a gravação continua no seu navegador.",
    "warnLimit": "A gravação termina automaticamente em 5 minutos (limite de 90 minutos).",
    "leaveWarning": "A gravação ainda não foi enviada. Se sair agora, o áudio será perdido.",
    "sending": "Enviando o áudio…",
    "queued": "Na fila para transcrição (posição {{position}}).",
    "transcribing": "Transcrevendo…",
    "failed": {
      "no_speech": "Nenhuma fala foi detectada. Grave novamente.",
      "transcription_error": "Não foi possível transcrever esta gravação.",
      "server_restart": "O servidor reiniciou durante o processamento e o áudio foi descartado. Grave novamente."
    },
    "recordAgain": "Gravar novamente",
    "errors": {
      "permission": "É necessário acesso ao microfone.",
      "device": "Nenhum microfone encontrado.",
      "connect": "Não foi possível conectar ao serviço de transcrição.",
      "invalid": "Formato ou idioma inválido.",
      "not_found": "Paciente ou consulta não encontrados.",
      "too_long": "Limite de 90 minutos atingido.",
      "finalize": "Não foi possível concluir a gravação."
    }
  },
```

Also add the note-page keys used in Task 10:

```json
  "noteView": {
    "processing": "Esta nota ainda está sendo processada.",
    "failed": "Falha ao processar esta nota.",
    "transcript": "Transcrição",
    "editorSoon": "A estruturação e a edição da nota chegam nas próximas entregas.",
    "notFound": "Nota não encontrada."
  },
```

- [ ] **Step 2: Steps.**

`components/live-session/PrepareStep.tsx`:

```tsx
import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { LiveLanguage, LiveNoteFormat } from "@anamnys/shared/lib/types";
import PatientPicker from "@/components/calendar/PatientPicker";
import { PRACTICE_ZONE, formatTime, todayIn } from "@/components/calendar/calendarTime";
import { inputClass } from "@/components/patients/FormField";
import { useAppointmentsQuery } from "@/hooks/useAppointments";
import type { StartOptions } from "@/hooks/useLiveRecorder";

const FORMATS: LiveNoteFormat[] = ["DAP", "SOAP", "Biopsychosocial"];
const LANGUAGES: LiveLanguage[] = ["pt", "en", "es"];

export default function PrepareStep({ onStart }: { onStart: (o: StartOptions) => void }) {
  const { t } = useTranslation();
  const [patient, setPatient] = useState<{ id: string; name: string } | null>(null);
  const [appointmentId, setAppointmentId] = useState<string>("");
  const [format, setFormat] = useState<LiveNoteFormat>("DAP");
  const [language, setLanguage] = useState<LiveLanguage>("pt");
  const today = useAppointmentsQuery([todayIn(PRACTICE_ZONE)]);
  const appointments = (today.data ?? []).filter(
    (a) => a.patientId === patient?.id && (a.status === "scheduled" || a.status === "confirmed"),
  );

  return (
    <div className="flex flex-col gap-4">
      <p className="rounded-radii-md bg-surfaceVariant p-3 text-body-md text-onSurfaceVariant">{t("liveSession.consent")}</p>
      <label className="flex flex-col gap-1 text-label-md">
        {t("liveSession.patient")}
        <PatientPicker value={patient} onChange={(p) => { setPatient(p); setAppointmentId(""); }} />
      </label>
      {patient && (
        <label className="flex flex-col gap-1 text-label-md">
          {t("liveSession.appointment")}
          <select className={inputClass} value={appointmentId} onChange={(e) => setAppointmentId(e.target.value)}>
            <option value="">{t("liveSession.noAppointment")}</option>
            {appointments.map((a) => (
              <option key={a.id} value={a.id}>{formatTime(new Date(a.startsAt), PRACTICE_ZONE)}</option>
            ))}
          </select>
        </label>
      )}
      <div className="flex gap-4">
        <label className="flex flex-1 flex-col gap-1 text-label-md">
          {t("liveSession.format")}
          <select className={inputClass} value={format} onChange={(e) => setFormat(e.target.value as LiveNoteFormat)}>
            {FORMATS.map((f) => <option key={f} value={f}>{t(`liveSession.formats.${f}`)}</option>)}
          </select>
        </label>
        <label className="flex flex-1 flex-col gap-1 text-label-md">
          {t("liveSession.language")}
          <select className={inputClass} value={language} onChange={(e) => setLanguage(e.target.value as LiveLanguage)}>
            {LANGUAGES.map((l) => <option key={l} value={l}>{t(`liveSession.languages.${l}`)}</option>)}
          </select>
        </label>
      </div>
      <button
        type="button"
        disabled={!patient}
        onClick={() => onStart({ patientId: patient!.id, appointmentId: appointmentId || null, format, language })}
        className="rounded-radii-md bg-primary px-4 py-3 text-label-lg text-onPrimary disabled:opacity-50"
      >
        {t("liveSession.start")}
      </button>
    </div>
  );
}
```

Before writing, open `AppointmentFormModal.tsx` and copy its exact class names for labels, selects and the primary button; the ones above are placeholders for that file's conventions only if they differ.

`components/live-session/RecordingStep.tsx`:

```tsx
import { useTranslation } from "react-i18next";
import { WARN_AT_MS, type LiveState } from "@/hooks/useLiveRecorder";

interface Props {
  state: LiveState;
  connection: "connected" | "connecting";
  elapsedMs: number;
  level: number;
  onPause: () => void;
  onResume: () => void;
  onStop: () => void;
}

function clock(ms: number): string {
  const total = Math.floor(ms / 1000);
  const h = Math.floor(total / 3600);
  const m = String(Math.floor((total % 3600) / 60)).padStart(2, "0");
  const s = String(total % 60).padStart(2, "0");
  return h > 0 ? `${h}:${m}:${s}` : `${m}:${s}`;
}

export default function RecordingStep({ state, connection, elapsedMs, level, onPause, onResume, onStop }: Props) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col items-center gap-6">
      <p className={connection === "connected" ? "text-label-md text-onSurfaceVariant" : "text-label-md text-error"}>
        {connection === "connected" ? t("liveSession.connected") : t("liveSession.connecting")}
      </p>
      <p className="text-display-sm tabular-nums">{clock(elapsedMs)}</p>
      <div className="h-2 w-64 overflow-hidden rounded-full bg-surfaceVariant" aria-hidden>
        <div className="h-full bg-primary transition-[width]" style={{ width: `${Math.min(100, level * 140)}%` }} />
      </div>
      {state === "paused" && <p className="text-label-md">{t("liveSession.paused")}</p>}
      {elapsedMs >= WARN_AT_MS && <p className="text-body-md text-error">{t("liveSession.warnLimit")}</p>}
      <div className="flex gap-3">
        {state === "recording"
          ? <button type="button" onClick={onPause} className="rounded-radii-md border border-outlineVariant px-4 py-2">{t("liveSession.pause")}</button>
          : <button type="button" onClick={onResume} className="rounded-radii-md border border-outlineVariant px-4 py-2">{t("liveSession.resume")}</button>}
        <button type="button" onClick={onStop} className="rounded-radii-md bg-primary px-4 py-2 text-onPrimary">{t("liveSession.stop")}</button>
      </div>
    </div>
  );
}
```

`components/live-session/ProcessingStep.tsx`:

```tsx
import { useEffect, useState } from "react";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import type { NoteFailureReason } from "@anamnys/shared/lib/types";
import { progressHub } from "@/lib/signalr";

// Waits for the note: "sending" until finalize returns a note id, then queue/transcribing until
// NoteReady (navigates to the note) or NoteFailed.
export default function ProcessingStep({ noteId, onRecordAgain }: { noteId: string | null; onRecordAgain: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [position, setPosition] = useState(0);
  const [failed, setFailed] = useState<NoteFailureReason | null>(null);

  useEffect(() => {
    if (!noteId) return;
    let active = true;
    const ready = () => void navigate({ to: "/notes/$noteId", params: { noteId } });
    void (async () => {
      await progressHub.connect();
      progressHub.onProgress((id, _stage, queuePosition) => { if (id === noteId && active) setPosition(queuePosition); });
      progressHub.onReady((id) => { if (id === noteId && active) ready(); });
      progressHub.onFailed((id, reason) => { if (id === noteId && active) setFailed(reason); });
      const snapshot = await progressHub.subscribe(noteId);
      if (!active || !snapshot) return;
      if (snapshot.status === "ReadyForReview") ready();
      else if (snapshot.status === "Failed") setFailed(snapshot.failureReason ?? "transcription_error");
      else setPosition(snapshot.queuePosition);
    })();
    return () => { active = false; void progressHub.disconnect(); };
  }, [noteId, navigate]);

  if (failed) {
    return (
      <div className="flex flex-col items-center gap-4">
        <p className="text-body-lg text-error">{t(`liveSession.failed.${failed}`)}</p>
        <button type="button" onClick={onRecordAgain} className="rounded-radii-md bg-primary px-4 py-2 text-onPrimary">{t("liveSession.recordAgain")}</button>
      </div>
    );
  }
  return (
    <p className="text-center text-body-lg text-onSurfaceVariant">
      {!noteId ? t("liveSession.sending") : position > 0 ? t("liveSession.queued", { position }) : t("liveSession.transcribing")}
    </p>
  );
}
```

- [ ] **Step 3: Route** — `routes/_app/live-session.tsx`:

```tsx
import { useEffect, useState } from "react";
import { createFileRoute, useBlocker } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import PrepareStep from "@/components/live-session/PrepareStep";
import RecordingStep from "@/components/live-session/RecordingStep";
import ProcessingStep from "@/components/live-session/ProcessingStep";
import { useLiveRecorder } from "@/hooks/useLiveRecorder";

export const Route = createFileRoute("/_app/live-session")({
  component: LiveSessionPage,
});

// Live dictation → transcript (design/specs/2026-10-10-live-session-transcription-design.md §6).
// No PHI in the URL: patient and appointment stay in component state.
function LiveSessionPage() {
  const { t } = useTranslation();
  const recorder = useLiveRecorder();
  const holdsAudio = recorder.state === "recording" || recorder.state === "paused" || recorder.state === "finalizing";

  useBlocker({
    shouldBlockFn: () => holdsAudio && !window.confirm(t("liveSession.leaveWarning")),
    enableBeforeUnload: () => holdsAudio,
  });

  useEffect(() => {
    if (recorder.error === "too_long") void recorder.stop();
  }, [recorder.error, recorder]);

  return (
    <div className="mx-auto max-w-xl p-4 md:p-8">
      <h1 className="mb-6 text-headline-sm">{t("liveSession.title")}</h1>
      {recorder.error && recorder.error !== "too_long" && (
        <p className="mb-4 text-body-md text-error">{t(`liveSession.errors.${recorder.error}`)}</p>
      )}
      {(recorder.state === "idle" || recorder.state === "error") && <PrepareStep onStart={(o) => void recorder.start(o)} />}
      {(recorder.state === "recording" || recorder.state === "paused") && (
        <RecordingStep
          state={recorder.state}
          connection={recorder.connection}
          elapsedMs={recorder.elapsedMs}
          level={recorder.level}
          onPause={recorder.pause}
          onResume={recorder.resume}
          onStop={() => void recorder.stop()}
        />
      )}
      {(recorder.state === "finalizing" || recorder.state === "done") && (
        <ProcessingStep noteId={recorder.noteId} onRecordAgain={recorder.reset} />
      )}
    </div>
  );
}
```

Check the installed `@tanstack/react-router` version's `useBlocker` signature (`shouldBlockFn` / `enableBeforeUnload` exist from 1.80+; the app uses 1.170). Drop the unused `useState` import.

- [ ] **Step 4: Regenerate the route tree.** In `apps/provider`: `npx vite build`. Confirm `routeTree.gen.ts` now contains `/_app/live-session`.

- [ ] **Step 5: Build and lint.** From `anamnys-aspire/`: `npm run build` and `npm run lint`. Expected: success.

- [ ] **Step 6: Stage for review.**

---

### Task 10: Note page shows status and transcript

**Files:**
- Modify: `apps/provider/src/routes/_app/notes/$noteId.tsx`

**Interfaces:**
- Consumes: `notesApi.getDetail` (Task 8), `noteView.*` and `liveSession.failed.*` i18n keys (Task 9).

- [ ] **Step 1: Replace the placeholder.**

```tsx
import { useQuery } from "@tanstack/react-query";
import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { notesApi } from "@anamnys/shared/api/notes";

export const Route = createFileRoute("/_app/notes/$noteId")({
  component: NotePage,
});

// Delivery 1 of the live session: state and raw transcript only. The editor is delivery 3
// (design/specs/2026-10-10-live-session-transcription-design.md).
function NotePage() {
  const { t } = useTranslation();
  const { noteId } = Route.useParams();
  const query = useQuery({
    queryKey: ["note", noteId],
    queryFn: () => notesApi.getDetail(noteId),
    refetchInterval: (q) => (q.state.data?.status === "Processing" ? 5000 : false),
  });

  if (query.isError) return <p className="p-8 text-center text-body-lg text-error">{t("noteView.notFound")}</p>;
  if (!query.data) return null;
  const note = query.data;

  return (
    <div className="mx-auto max-w-3xl p-4 md:p-8">
      <h1 className="text-headline-sm">{note.patientName}</h1>
      <p className="mb-6 text-label-md text-onSurfaceVariant">
        {new Date(note.createdAt).toLocaleString("pt-BR")} · {note.format}
      </p>
      {note.status === "Processing" && <p className="text-body-lg text-onSurfaceVariant">{t("noteView.processing")}</p>}
      {note.status === "Failed" && (
        <p className="text-body-lg text-error">
          {note.failureReason ? t(`liveSession.failed.${note.failureReason}`) : t("noteView.failed")}
        </p>
      )}
      {note.rawTranscript && (
        <section>
          <h2 className="mb-2 text-title-md">{t("noteView.transcript")}</h2>
          <p className="whitespace-pre-wrap rounded-radii-md bg-surfaceVariant p-4 text-body-md">{note.rawTranscript}</p>
          <p className="mt-4 text-body-sm text-onSurfaceVariant">{t("noteView.editorSoon")}</p>
        </section>
      )}
    </div>
  );
}
```

The note page polls every 5 s while `Processing` instead of opening the progress hub; the live-session screen already handles the live wait.

- [ ] **Step 2: Build and lint.** `npm run build` and `npm run lint`. Expected: success.

- [ ] **Step 3: Stage for review.**

---

### Task 11: End-to-end verification and docs

**Files:**
- Modify: `design/gaps/2026-10-03-patient-flows-gaps.md`, `CLAUDE.md`, `design/specs/2026-10-10-live-session-transcription-design.md` (status)

- [ ] **Step 1: Full test suite.** `aspire stop`, then `dotnet run --project anamnys-aspire.Tests`. Expected: all PASS (Whisper real-model test skipped unless `ANAMNYS_WHISPER_MODEL` is set).

- [ ] **Step 2: Manual run** (`aspire start`, provider app at `http://localhost:5273/provider/`, `dev.provider`):
  1. Side nav → "Sessão ao Vivo"; consent notice visible.
  2. Pick a patient with an appointment today; the appointment appears in the select.
  3. Record ~30 s of Portuguese speech, pause 5 s, resume, stop. Expected: "Enviando…" → "Transcrevendo…" → note page with the transcript; first run downloads `ggml-base.bin` (log line "Downloading Whisper model").
  4. Database check: the note has `InputMode = VoiceLive`, a `SessionId`; the appointment now has the same `SessionId`; the session is `attended` with `ActualStart`/`ActualEnd`.
  5. Record again and, mid-recording, kill the network in DevTools (Offline) for 20 s, then restore. Expected: "Reconectando…", then the full audio is transcribed (no gap in the text).
  6. Record 3 s of silence. Expected: "Nenhuma fala foi detectada".
  7. Start recording and try to navigate to Agenda. Expected: confirmation dialog; cancelling keeps recording.
  8. Firefox: repeat step 3 (resampling path).
  9. Check the server logs: no transcript text anywhere.

- [ ] **Step 3: Docs.**
  - Gaps file: under "Videochamada", nothing changes; under "Notas clínicas", replace "nothing creates a `Sessions` row yet" with what now creates one (live session finalize) and add new gaps: structuring (delivery 2), editor/sign (delivery 3), no `AccessLogs`/`AuditEntries` writes for notes yet, production model directory must be a persistent volume, production `LiveSession:ModelSha256` must be set before deploy.
  - `CLAUDE.md` "Backend — present": add "Whisper.net (CPU runtime) for in-process transcription; models downloaded on first use to `LiveSession:ModelDirectory`, verified by `LiveSession:ModelSha256`" and "ASP.NET Core SignalR hubs `/hubs/transcription` and `/hubs/progress`".
  - Spec status: `Approved` → `Implemented`.
  - Test runs in Development load `appsettings.Development.json`, so the server under test downloads `ggml-base.bin` on the first transcription job if it is not already in the model directory; note this in the gaps file under "Qualidade" if it slows CI.

- [ ] **Step 4: Stage for review and hand over.** Summarise for the user what was verified and what was not (e.g. Safari untested), and wait for their go-ahead before any commit.
