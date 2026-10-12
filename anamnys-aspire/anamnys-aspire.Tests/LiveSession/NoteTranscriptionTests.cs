using Anamnys.Server.Data;
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
        note.StructureStatus.Should().BeNull();
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
        note.StructureStatus.Should().BeNull();
        bytes.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public async Task Process_ZeroesDecodedSamplesAfterTranscribing()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var (db, job, _) = await QueuedJobAsync(seed);
        await using var _ = db;
        float[]? captured = null;

        await NoteTranscription.ProcessAsync(db, new FakeTranscriber(s => { captured = s; return "texto"; }), job,
            new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        captured.Should().NotBeNull();
        captured!.Should().NotBeEmpty().And.OnlyContain(x => x == 0f);
    }

    // Finalizes a real recording so the note and session exist as in production; returns the
    // job's backing bytes so the test can check they were zeroed.
    private static async Task<(AnamnysDbContext Db, TranscriptionJob Job, byte[] Bytes)> QueuedJobAsync(PatientSearchSeed seed)
    {
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var db = seed.CreateDbContext();
        var buffers = new RecordingBuffers(new FakeTimeProvider(Now));
        var queue = new TranscriptionQueue();
        var id = Guid.NewGuid();
        buffers.Begin(id, new RecordingMeta(seed.ProviderId, patient, null, "DAP", "pt", Now.AddMinutes(-10)));
        buffers.Append(id, seed.ProviderId, 0, [1, 0, 2, 0]);
        await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, id, Now, Ct);
        queue.Reader.TryRead(out var read).Should().BeTrue();
        var queued = read.Should().BeOfType<TranscriptionJob>().Subject;
        var bytes = new byte[] { 1, 0, 2, 0 };
        queued.Audio.Destroy();
        return (db, queued with { Audio = new PcmAudio(bytes, bytes.Length) }, bytes);
    }
}
