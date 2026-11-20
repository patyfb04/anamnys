using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Anamnys.Server.Data;
using Anamnys.Server.LiveSession;
using Anamnys.Server.Notes;
using Anamnys.Server.Pseudonymization;
using Anamnys.Tests.Pseudonymization;
using Anamnys.Tests.RecordingConsents;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Anamnys.Tests.RecordingConsents.ConsentScene;

namespace Anamnys.Tests.LiveSession;

// F-26 (design/specs/2026-11-16-transient-audio-tab-capture-design.md §2, §4): after a completed recording, a
// transcriber failure and a swept (abandoned) buffer, no audio artefact survives anywhere — the buffer entry is gone
// and its bytes are zero, no table can hold audio (bytea whitelist), the object store has no object of the recording,
// no new temp file holds the audio — and one AudioDestructionLogs row with its audit entry records the end of each server
// copy of the audio (a recording swept during a long pause and re-sent ends with an abandoned and a final row).
// Uses the consent scene's scratch database (the bootstrap script carries the migration).
[Collection(SharedAppHostCollection.Name)]
public class AudioDestructionTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The only bytea columns of the schema: token hashes, the timestamp authority's tokens and the F-27 pseudonym secret
    // and fingerprints (32-byte values). None holds audio.
    private static readonly string[] ByteaWhitelist =
    [
        "AppointmentConfirmationTokens.TokenHash", "Dossiers.AnchorToken", "IntegrityAnchors.Token", "PatientInvitations.TokenHash",
        "RecordingConsents.RevokeTokenHash", "RecordingConsents.SignTokenHash", "PatientPseudonymSecrets.Protected", "PseudonymTokens.Fingerprint",
    ];

    private static PseudonymizationStep Step() => PseudonymizationKit.Step(clock: new FakeTimeProvider(Later));

    static AudioDestructionTests() => QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    [Fact]
    public async Task CompletedSession_LeavesNoAudioAnywhere_AndRecordsTranscribed()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SignedAsync(s.Adult, Ct);
        var before = await SnapshotAsync();
        // More than the buffer's first 64 KiB, so the growth path runs too.
        var rec = await RecordAsync(s, s.Adult, LiveRecordings.SessionMode, chunks: 45);
        var job = await FinalizeAsync(s, rec);
        float[]? samples = null;

        // Act
        await using (var db = s.CreateDbContext())
        {
            var outcome = await NoteTranscription.ProcessAsync(db, new FakeTranscriber(x => { samples = x; return "Paciente relata melhora."; }), Step(), job,
                new FakeTimeProvider(Later), NullLogger.Instance, Ct);
            outcome.Should().Be(TranscriptionOutcome.Ready);
        }

        // Assert
        rec.Grown.Should().NotBeNull();
        samples.Should().NotBeNullOrEmpty().And.OnlyContain(x => x == 0f);
        await AssertNoAudioSurvivesAsync(s, rec, before, job.NoteId);
        await AssertDestructionRowAsync(s, rec, job.NoteId, AudioDestruction.Transcribed, LiveRecordings.SessionMode);
    }

    [Fact]
    public async Task SessionSwitchedMidRecording_IsOneTranscription_AndTheRowKeepsEverySourceInOrder()
    {
        // Arrange: online capture first, the speaker method after chunk 2 (4 chunks in all).
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SignedAsync(s.Adult, Ct);
        var rec = await RecordAsync(s, s.Adult, LiveRecordings.SessionMode, chunks: 4,
            source: RecordingSources.OnlineCapture, switchTo: RecordingSources.OnlineSpeaker);
        var job = await FinalizeAsync(s, rec);
        var lengths = new List<int>();
        var transcriber = new FakeTranscriber(x => { lengths.Add(x.Length); return "Paciente relata melhora."; });

        // Act
        await using (var db = s.CreateDbContext())
        {
            (await NoteTranscription.ProcessAsync(db, transcriber, Step(), job, new FakeTimeProvider(Later), NullLogger.Instance, Ct))
                .Should().Be(TranscriptionOutcome.Ready);
        }

        // Assert: the chunks before and after the switch are one audio, transcribed once.
        transcriber.Calls.Should().Be(1);
        lengths.Should().Equal(4 * ChunkBytes / 2);
        job.Meta.Sources.Should().Equal(RecordingSources.OnlineCapture, RecordingSources.OnlineSpeaker);
        await AssertDestructionRowAsync(s, rec, job.NoteId, AudioDestruction.Transcribed, LiveRecordings.SessionMode,
            sources: [RecordingSources.OnlineCapture, RecordingSources.OnlineSpeaker]);
        await using var read = s.CreateDbContext();
        var switched = await read.AuditEntries.AsNoTracking().Where(a => a.Action == LiveRecordings.SourceSwitchedAction).ToListAsync(Ct);
        switched.Should().ContainSingle().Which.NewValue.Should().Be($"{rec.Id} {RecordingSources.OnlineSpeaker}");
    }

    [Fact]
    public async Task TranscriberFailure_LeavesNoAudioAnywhere_AndRecordsFailed()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var before = await SnapshotAsync();
        var rec = await RecordAsync(s, s.Adult, LiveRecordings.DictationMode);
        var job = await FinalizeAsync(s, rec);
        float[]? samples = null;
        // A native transcriber's message could echo its input: it must never reach the log.
        const string echo = "boom-trecho-do-audio";
        var logger = new CapturingLogger();

        // Act
        await using (var db = s.CreateDbContext())
        {
            var outcome = await NoteTranscription.ProcessAsync(db, new FakeTranscriber(x => { samples = x; throw new InvalidOperationException(echo); }), Step(), job,
                new FakeTimeProvider(Later), logger, Ct);
            outcome.Should().Be(TranscriptionOutcome.Error);
        }

        // Assert
        samples.Should().NotBeNullOrEmpty().And.OnlyContain(x => x == 0f);
        logger.Messages.Should().NotBeEmpty();
        logger.Messages.Should().NotContain(m => m.Contains(echo));
        logger.Messages.Should().OnlyContain(m => m == $"Transcription failed for note {job.NoteId}: {nameof(InvalidOperationException)}");
        await AssertNoAudioSurvivesAsync(s, rec, before, job.NoteId);
        await AssertDestructionRowAsync(s, rec, job.NoteId, AudioDestruction.Failed, LiveRecordings.DictationMode);
    }

    [Fact]
    public async Task NoSpeech_RecordsFailed()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var rec = await RecordAsync(s, s.Adult, LiveRecordings.DictationMode);
        var job = await FinalizeAsync(s, rec);

        // Act
        await using (var db = s.CreateDbContext())
        {
            await NoteTranscription.ProcessAsync(db, new FakeTranscriber(_ => " "), Step(), job, new FakeTimeProvider(Later), NullLogger.Instance, Ct);
        }

        // Assert
        rec.Bytes.Should().OnlyContain(b => b == 0);
        await AssertDestructionRowAsync(s, rec, job.NoteId, AudioDestruction.Failed, LiveRecordings.DictationMode);
    }

    [Fact]
    public async Task SweptBuffer_LeavesNoAudioAnywhere_AndRecordsAbandoned()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SignedAsync(s.Adult, Ct);
        var before = await SnapshotAsync();
        var rec = await RecordAsync(s, s.Adult, LiveRecordings.SessionMode);
        rec.Clock.Advance(LiveSessionRules.BufferIdleTimeout + TimeSpan.FromMinutes(1));
        using var services = Services(s, new FakeTranscriber(_ => "x"));
        var sweeper = new RecordingBufferSweeper(rec.Buffers, services.GetRequiredService<IServiceScopeFactory>(), rec.Clock,
            NullLogger<RecordingBufferSweeper>.Instance);

        // Act
        var swept = await sweeper.SweepAsync(Ct);

        // Assert
        swept.Should().Be(1);
        await AssertNoAudioSurvivesAsync(s, rec, before, null);
        await AssertDestructionRowAsync(s, rec, null, AudioDestruction.Abandoned, LiveRecordings.SessionMode, rec.Clock.GetUtcNow());
    }

    [Fact]
    public async Task WorkerFailingBeforeTranscription_RecordsFailed()
    {
        // Arrange: the first progress notification throws, so the transcription never runs.
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var rec = await RecordAsync(s, s.Adult, LiveRecordings.DictationMode);
        var job = await FinalizeAsync(s, rec);
        using var services = Services(s, new FakeTranscriber(_ => "x"));

        // Act
        await TranscriptionWorker.RunJobAsync(job, services.GetRequiredService<IServiceScopeFactory>(), new ThrowingHubContext(),
            new FakeTimeProvider(Later), NullLogger.Instance, Ct);

        // Assert
        rec.Bytes.Should().OnlyContain(b => b == 0);
        await AssertDestructionRowAsync(s, rec, job.NoteId, AudioDestruction.Failed, LiveRecordings.DictationMode);
    }

    [Fact]
    public async Task NoteView_ReturnsTheDestruction_OnlyForTheOwnersRecordedNote()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var rec = await RecordAsync(s, s.Adult, LiveRecordings.DictationMode);
        var job = await FinalizeAsync(s, rec);
        await using (var db = s.CreateDbContext())
        {
            await NoteTranscription.ProcessAsync(db, new FakeTranscriber(_ => "texto"), Step(), job, new FakeTimeProvider(Later), NullLogger.Instance, Ct);
        }
        var typed = await s.Db.AddNoteAsync(s.Provider, s.Adult, Later, Ct);

        // Act
        await using var read = s.CreateDbContext();
        var recorded = await NoteQueries.GetAsync(read, s.Provider, job.NoteId, Ct);
        var plain = await NoteQueries.GetAsync(read, s.Provider, typed, Ct);
        var foreign = await NoteQueries.GetAsync(read, s.OtherProvider, job.NoteId, Ct);

        // Assert
        recorded!.AudioDestroyedAt.Should().Be(Later);
        recorded.AudioOutcome.Should().Be(AudioDestruction.Transcribed);
        recorded.AudioSources.Should().Equal(RecordingSources.InPerson);
        plain!.AudioDestroyedAt.Should().BeNull();
        plain.AudioOutcome.Should().BeNull();
        plain.AudioSources.Should().BeNull();
        foreign.Should().BeNull();
    }

    [Fact]
    public async Task TryRecordAsync_WhenTheRowCannotBeWritten_ReturnsFalseAndLeavesTheContextClean()
    {
        // Arrange: a patient of another provider breaks the patient foreign key.
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var meta = LiveRecordings.NewMeta(s.Provider, s.Stranger, null, "DAP", "pt", LiveRecordings.DictationMode, RecordingSources.InPerson, Later);
        await using var db = s.CreateDbContext();

        var recordingId = Guid.NewGuid();
        var logger = new CapturingLogger();

        // Act
        var recorded = await AudioDestruction.TryRecordAsync(db, recordingId, meta, null, AudioDestruction.Abandoned, Later, logger, Ct);

        // Assert
        recorded.Should().BeFalse();
        db.ChangeTracker.Entries().Should().BeEmpty();
        logger.Messages.Should().Equal($"Audio destruction record failed for recording {recordingId}: DbUpdateException");
    }

    [Fact]
    public async Task FinalizeFailure_LeavesNoAudioAnywhere_AndRecordsFailedWithoutNote()
    {
        // Arrange: the meta points at an appointment that does not exist, so the finalize transaction throws.
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var before = await SnapshotAsync();
        var rec = await RecordAsync(s, s.Adult, LiveRecordings.DictationMode, appointmentId: Guid.NewGuid());
        var queue = new TranscriptionQueue();
        var logger = new CapturingLogger();

        // Act
        await using (var db = s.CreateDbContext())
        {
            var act = () => LiveRecordings.FinalizeAsync(db, rec.Buffers, queue, s.Provider, rec.Id, Later, Ct, logger);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        // Assert
        queue.Reader.TryRead(out _).Should().BeFalse();
        logger.Messages.Should().BeEmpty();
        await AssertNoAudioSurvivesAsync(s, rec, before, null);
        await AssertDestructionRowAsync(s, rec, null, AudioDestruction.Failed, LiveRecordings.DictationMode);
    }

    [Fact]
    public async Task BeginAgainAfterFinalize_DestroysTheReopenedBuffer_AndKeepsOneRow()
    {
        // Arrange: a reconnect retries the begin (and a chunk) for a recording already finalized and transcribed.
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var rec = await RecordAsync(s, s.Adult, LiveRecordings.DictationMode);
        var job = await FinalizeAsync(s, rec);
        await using (var db = s.CreateDbContext())
        {
            await NoteTranscription.ProcessAsync(db, new FakeTranscriber(_ => "texto"), Step(), job, new FakeTimeProvider(Later), NullLogger.Instance, Ct);
        }
        rec.Buffers.Begin(rec.Id, LiveRecordings.NewMeta(s.Provider, s.Adult, null, "DAP", "pt", LiveRecordings.DictationMode, RecordingSources.InPerson, Later))
            .Should().Be(BeginStatus.Opened);
        rec.Buffers.Append(rec.Id, s.Provider, 0, rec.Marker.ToArray());
        var reopened = rec.Buffers.BackingBytes(rec.Id)!;

        // Act
        FinalizeResult again;
        await using (var db = s.CreateDbContext())
        {
            again = await LiveRecordings.FinalizeAsync(db, rec.Buffers, new TranscriptionQueue(), s.Provider, rec.Id, Later, Ct, NullLogger.Instance);
        }
        rec.Clock.Advance(LiveSessionRules.BufferIdleTimeout + TimeSpan.FromMinutes(1));
        using var services = Services(s, new FakeTranscriber(_ => "x"));
        var swept = await new RecordingBufferSweeper(rec.Buffers, services.GetRequiredService<IServiceScopeFactory>(), rec.Clock,
            NullLogger<RecordingBufferSweeper>.Instance).SweepAsync(Ct);

        // Assert
        again.Should().Be(new FinalizeResult(FinalizeStatus.Ok, job.NoteId));
        reopened.Should().OnlyContain(b => b == 0);
        rec.Buffers.BackingBytes(rec.Id).Should().BeNull();
        swept.Should().Be(0);
        await AssertDestructionRowAsync(s, rec, job.NoteId, AudioDestruction.Transcribed, LiveRecordings.DictationMode);
    }

    [Fact]
    public async Task RecordingSweptThenReopenedWithTheSameId_EndsWithTwoRows_AndNoAudioLeft()
    {
        // Arrange: a long pause sweeps the buffer (abandoned); the client re-begins the same id and re-sends everything.
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SignedAsync(s.Adult, Ct);
        var before = await SnapshotAsync();
        var rec = await RecordAsync(s, s.Adult, LiveRecordings.SessionMode);
        rec.Clock.Advance(LiveSessionRules.BufferIdleTimeout + TimeSpan.FromMinutes(1));
        var sweptAt = rec.Clock.GetUtcNow();
        using var services = Services(s, new FakeTranscriber(_ => "x"));
        var swept = await new RecordingBufferSweeper(rec.Buffers, services.GetRequiredService<IServiceScopeFactory>(), rec.Clock,
            NullLogger<RecordingBufferSweeper>.Instance).SweepAsync(Ct);
        rec.Buffers.Begin(rec.Id, LiveRecordings.NewMeta(s.Provider, s.Adult, null, "DAP", "pt", LiveRecordings.SessionMode, RecordingSources.InPerson, Later.AddMinutes(-20)))
            .Should().Be(BeginStatus.Opened);
        var chunk = Enumerable.Repeat(rec.Marker, ChunkBytes / rec.Marker.Length).SelectMany(b => b).ToArray();
        for (var seq = 0; seq < 2; seq++)
        {
            rec.Buffers.Append(rec.Id, s.Provider, seq, chunk.ToArray()).Status.Should().Be(ChunkStatus.Accepted);
        }
        var reopened = rec.Buffers.BackingBytes(rec.Id)!;
        var finalizedAt = sweptAt.AddMinutes(5);
        var queue = new TranscriptionQueue();
        await using (var db = s.CreateDbContext())
        {
            (await LiveRecordings.FinalizeAsync(db, rec.Buffers, queue, s.Provider, rec.Id, finalizedAt, Ct, NullLogger.Instance))
                .Status.Should().Be(FinalizeStatus.Ok);
        }
        queue.Reader.TryRead(out var queued).Should().BeTrue();
        var job = (TranscriptionJob)queued!;

        // Act
        await using (var db = s.CreateDbContext())
        {
            (await NoteTranscription.ProcessAsync(db, new FakeTranscriber(_ => "Paciente relata melhora."), Step(), job, new FakeTimeProvider(finalizedAt),
                NullLogger.Instance, Ct)).Should().Be(TranscriptionOutcome.Ready);
        }

        // Assert: one row per destroyed server copy, and both copies are zeroed.
        swept.Should().Be(1);
        reopened.Should().OnlyContain(b => b == 0);
        await AssertNoAudioSurvivesAsync(s, rec, before, job.NoteId);
        await using var read = s.CreateDbContext();
        var rows = await read.AudioDestructionLogs.AsNoTracking().Where(r => r.SessionId == rec.Id).OrderBy(r => r.DestroyedAt).ToListAsync(Ct);
        rows.Select(r => (r.Outcome, r.NoteId, r.DestroyedAt)).Should().Equal(
            (AudioDestruction.Abandoned, (Guid?)null, sweptAt), (AudioDestruction.Transcribed, (Guid?)job.NoteId, finalizedAt));
        (await read.AudioDestructionLogs.CountAsync(r => r.ProviderId == s.Provider, Ct)).Should().Be(2);
        var audit = await read.AuditEntries.AsNoTracking().Where(a => a.Action == AudioDestruction.AuditAction).OrderBy(a => a.At).ToListAsync(Ct);
        audit.Select(a => a.NewValue).Should().Equal($"{rec.Id} - {AudioDestruction.Abandoned}", $"{rec.Id} {job.NoteId} {AudioDestruction.Transcribed}");
        // The note view shows the latest row of the note (the abandoned row has no note).
        var view = await NoteQueries.GetAsync(read, s.Provider, job.NoteId, Ct);
        view!.AudioOutcome.Should().Be(AudioDestruction.Transcribed);
        view.AudioDestroyedAt.Should().Be(finalizedAt);
    }

    // Grown: the buffer's first backing array when the audio outgrew it (null otherwise).
    private sealed record Recording(
        Guid Id, Guid Patient, RecordingBuffers Buffers, FakeTimeProvider Clock, byte[] Bytes, byte[] Marker, byte[]? Grown);

    private const int ChunkBytes = 1600;

    // Begins as the hub does and sends chunks of a recognisable pattern (more than 64 KiB grows the buffer); returns the
    // buffer's backing bytes. The appointment, when given, goes to the meta only (the check runs without it).
    private static async Task<Recording> RecordAsync(
        ConsentScene s, Guid patient, string mode, int chunks = 2, Guid? appointmentId = null,
        string source = RecordingSources.InPerson, string? switchTo = null)
    {
        var clock = new FakeTimeProvider(Later);
        var buffers = new RecordingBuffers(clock);
        await using (var db = s.CreateDbContext())
        {
            var check = await LiveRecordings.CheckBeginAsync(db, s.Provider, patient, null, "DAP", "pt", mode, true, source, Later, Ct);
            check.Check.Should().Be(BeginCheck.Ok);
        }
        var id = Guid.NewGuid();
        // The session's start is the recording's start: before the finalize at Later.
        buffers.Begin(id, LiveRecordings.NewMeta(s.Provider, patient, appointmentId, "DAP", "pt", mode, source, Later.AddMinutes(-20)))
            .Should().Be(BeginStatus.Opened);
        var marker = RandomNumberGenerator.GetBytes(32);
        var chunk = Enumerable.Repeat(marker, ChunkBytes / marker.Length).SelectMany(b => b).ToArray();
        byte[]? initial = null;
        for (var seq = 0; seq < chunks; seq++)
        {
            if (switchTo is not null && seq == chunks / 2)
            {
                // The provider changes the method halfway: the same buffer and sequence go on.
                await using var db = s.CreateDbContext();
                (await LiveRecordings.SwitchSourceAsync(db, buffers, s.Provider, id, switchTo, Later, Ct)).Should().Be(SwitchStatus.Ok);
            }
            buffers.Append(id, s.Provider, seq, chunk.ToArray()).Status.Should().Be(ChunkStatus.Accepted);
            initial ??= buffers.BackingBytes(id);
        }
        var bytes = buffers.BackingBytes(id)!;
        bytes.AsSpan().IndexOf(marker).Should().BeGreaterThanOrEqualTo(0);
        var grown = ReferenceEquals(initial, bytes) ? null : initial;
        if (chunks * ChunkBytes > 64 * 1024)
        {
            // The growth path zeroes the outgrown array at once.
            grown.Should().NotBeNull();
            grown!.Should().OnlyContain(b => b == 0);
        }
        return new Recording(id, patient, buffers, clock, bytes, marker, grown);
    }

    private static async Task<TranscriptionJob> FinalizeAsync(ConsentScene s, Recording rec)
    {
        var queue = new TranscriptionQueue();
        await using var db = s.CreateDbContext();
        var result = await LiveRecordings.FinalizeAsync(db, rec.Buffers, queue, s.Provider, rec.Id, Later, Ct, NullLogger.Instance);
        result.Status.Should().Be(FinalizeStatus.Ok);
        queue.Reader.TryRead(out var job).Should().BeTrue();
        return (TranscriptionJob)job!;
    }

    // Formats every entry, so a test can check that no log line carries anything but ids and type names.
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
        }
    }

    private static ServiceProvider Services(ConsentScene s, ITranscriber transcriber) =>
        new ServiceCollection()
            .AddScoped(_ => s.CreateDbContext())
            .AddSingleton(transcriber)
            .AddSingleton(Step())
            .BuildServiceProvider();

    // Objects: key → ETag of every object in the bucket.
    private sealed record Snapshot(HashSet<string> TempFiles, DateTimeOffset At, Dictionary<string, string> Objects);

    private async Task<Snapshot> SnapshotAsync()
    {
        using var s3 = S3();
        return new Snapshot(TempFiles(), DateTimeOffset.UtcNow, (await ListObjectsAsync(s3)).ToDictionary(o => o.Key, o => o.ETag ?? ""));
    }

    private AmazonS3Client S3()
    {
        var options = fixture.StorageOptions;
        return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            new AmazonS3Config { ServiceURL = options.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
    }

    private async Task<List<S3Object>> ListObjectsAsync(AmazonS3Client s3)
    {
        var objects = new List<S3Object>();
        var request = new ListObjectsV2Request { BucketName = fixture.StorageOptions.Bucket };
        ListObjectsV2Response page;
        do
        {
            page = await s3.ListObjectsV2Async(request, Ct);
            objects.AddRange(page.S3Objects ?? []);
            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated == true);
        return objects;
    }

    // Reads an object (bounded as the temp files are) and looks for the audio marker; a vanished object holds nothing.
    private async Task<bool> ObjectHoldsAsync(AmazonS3Client s3, S3Object o, byte[] marker)
    {
        if (o.Size > 64 * 1024 * 1024)
        {
            return false;
        }
        try
        {
            using var response = await s3.GetObjectAsync(fixture.StorageOptions.Bucket, o.Key, Ct);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, Ct);
            return buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf(marker) >= 0;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static HashSet<string> TempFiles() =>
        [.. Directory.EnumerateFiles(Path.GetTempPath(), "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
        })];

    private async Task AssertNoAudioSurvivesAsync(ConsentScene s, Recording rec, Snapshot before, Guid? noteId)
    {
        // The buffer entry is gone and its bytes are zero.
        rec.Buffers.Peek(rec.Id, s.Provider).Should().BeNull();
        rec.Buffers.BackingBytes(rec.Id).Should().BeNull();
        rec.Bytes.Should().OnlyContain(b => b == 0);
        rec.Grown?.Should().OnlyContain(b => b == 0);

        // No table can hold audio: the only bytea columns are the known non-audio ones.
        List<string> bytea;
        await using (var db = s.CreateDbContext())
        {
            bytea = await db.Database.SqlQuery<string>($"""
                SELECT table_name || '.' || column_name AS "Value" FROM information_schema.columns
                 WHERE table_schema = 'public' AND data_type = 'bytea'
                """).ToListAsync(Ct);
        }
        bytea.Should().BeEquivalentTo(ByteaWhitelist);

        // The object store has no new object with the audio: other test classes write to the shared bucket in parallel,
        // so every object that appeared or changed since the snapshot is read and must not hold the audio marker; and no
        // key names this recording, note, patient or provider.
        var ids = new[] { rec.Id, rec.Patient, s.Provider }.Concat(noteId is { } n ? [n] : []).Select(i => i.ToString()).ToList();
        using (var s3 = S3())
        {
            var objects = await ListObjectsAsync(s3);
            objects.Should().NotContain(o => ids.Any(id => o.Key.Contains(id, StringComparison.OrdinalIgnoreCase)));
            var fresh = objects.Where(o => !before.Objects.TryGetValue(o.Key, out var etag) || etag != (o.ETag ?? "")).ToList();
            foreach (var o in fresh)
            {
                (await ObjectHoldsAsync(s3, o, rec.Marker)).Should().BeFalse($"object {o.Key} must not hold the audio");
            }
        }

        // No new temp file: other test classes create temp files in parallel, so every file that appeared or changed
        // since the snapshot is read and must not hold the audio.
        var changed = TempFiles().Where(f => !before.TempFiles.Contains(f) || SafeLastWrite(f) >= before.At).ToList();
        changed.Should().NotContain(f => Holds(f, rec.Marker));
    }

    private static DateTimeOffset SafeLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static bool Holds(string path, byte[] marker)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64 * 1024 * 1024)
            {
                return false;
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf(marker) >= 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task AssertDestructionRowAsync(ConsentScene s, Recording rec, Guid? noteId, string outcome, string mode,
        DateTimeOffset? at = null, string[]? sources = null)
    {
        await using var db = s.CreateDbContext();
        var row = await db.AudioDestructionLogs.AsNoTracking().SingleAsync(r => r.SessionId == rec.Id, Ct);
        row.ProviderId.Should().Be(s.Provider);
        row.PatientId.Should().Be(rec.Patient);
        row.NoteId.Should().Be(noteId);
        row.Outcome.Should().Be(outcome);
        row.RecordingMode.Should().Be(mode);
        row.Sources.Should().Equal(sources ?? [RecordingSources.InPerson]);
        row.DestroyedAt.Should().Be(at ?? Later);
        (await db.AudioDestructionLogs.CountAsync(r => r.ProviderId == s.Provider, Ct)).Should().Be(1);
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.Action == AudioDestruction.AuditAction).ToListAsync(Ct);
        audit.Should().ContainSingle();
        audit[0].ActorId.Should().Be(s.Provider);
        audit[0].NoteId.Should().BeNull();
        audit[0].NewValue.Should().Be($"{rec.Id} {(noteId?.ToString() ?? "-")} {outcome}");
    }

    private sealed class ThrowingHubContext : IHubContext<ProgressHub>, IHubClients, IClientProxy
    {
        IHubClients IHubContext<ProgressHub>.Clients => this;
        IGroupManager IHubContext<ProgressHub>.Groups => throw new NotSupportedException();
        IClientProxy IHubClients<IClientProxy>.All => this;
        IClientProxy IHubClients<IClientProxy>.AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => this;
        IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> connectionIds) => this;
        IClientProxy IHubClients<IClientProxy>.Group(string groupName) => this;
        IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groupNames) => this;
        IClientProxy IHubClients<IClientProxy>.GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
        IClientProxy IHubClients<IClientProxy>.User(string userId) => this;
        IClientProxy IHubClients<IClientProxy>.Users(IReadOnlyList<string> userIds) => this;

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            method == "NoteProgress" ? throw new InvalidOperationException("boom") : Task.CompletedTask;
    }
}
