using System.Net;
using Anamnys.Server.Data;
using Anamnys.Server.LiveSession;
using Anamnys.Server.Notes;
using Anamnys.Server.NoteStructuring;
using Anamnys.Server.Pseudonymization;
using Anamnys.Server.Themes;
using Anamnys.Tests.Auth;
using Anamnys.Tests.LiveSession;
using Anamnys.Tests.NoteStructuring;
using Anamnys.Tests.Retention;
using Anamnys.Tests.Themes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.Pseudonymization;

// The pipeline step (design/specs/2026-11-17-pseudonymization-design.md §2, §8): live transcript and "Nota com IA" go
// through it before RawTranscript is written; a failure writes nothing with names, fails the note and holds the text in
// memory for "Tentar de novo"; manual notes do not go through it. Scratch database; fake detector, transcriber and structurer.
[Collection(SharedAppHostCollection.Name)]
public class PseudonymizationStepTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string P = EntityKinds.Person;
    private const string Secret = "Segredo";

    [Fact]
    public async Task RunAsync_Success_WritesTokensReadyForReviewAndAuditsCountsOnly()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct);
        var holds = new PseudonymizationHolds(new FakeTimeProvider(Now));
        var step = PseudonymizationKit.Step(FakeThirdPartyDetector.Finding(($"Joana {Secret}", P)), new FakeTimeProvider(Now), holds: holds);
        await using var db = s.CreateDbContext();

        // Act
        var outcome = await step.RunAsync(db, note, provider, patient, $"Joana {Secret} ligou.", Ct);

        // Assert
        outcome.Should().Be(PseudonymizationOutcome.Done);
        var row = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct);
        row.RawTranscript.Should().Be("[P1] ligou.");
        row.TranscriptPseudonymized.Should().BeTrue();
        row.Status.Should().Be("ReadyForReview");
        row.StructureStatus.Should().Be("pending");
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.NoteId == note && a.Action == PseudonymizationStep.DoneAction, Ct);
        audit.NewValue.Should().Be("person=1,organization=0,school=0,address=0");
        (await PseudonymizationScratch.NameAppearsAnywhereAsync(s, Secret)).Should().BeFalse();
        var view = await NoteQueries.GetAsync(db, provider, note, Ct, holds);
        view!.TranscriptPseudonymized.Should().BeTrue();
        view.PseudonymizationRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_DetectorError_FailsTheNote_WritesNoName_HoldsTheText_AndLogsIdsOnly()
    {
        // Arrange: the detector's exception message echoes the text.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct);
        var logger = new CapturingLogger<PseudonymizationStep>();
        var holds = new PseudonymizationHolds(new FakeTimeProvider(Now));
        var step = PseudonymizationKit.Step(new FakeThirdPartyDetector(fail: new InvalidOperationException($"Joana {Secret}")),
            new FakeTimeProvider(Now), logger, holds);
        await using var db = s.CreateDbContext();

        // Act
        var outcome = await step.RunAsync(db, note, provider, patient, $"Joana {Secret} ligou.", Ct);

        // Assert
        outcome.Should().Be(PseudonymizationOutcome.Failed);
        var row = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct);
        row.Status.Should().Be("Failed");
        row.FailureReason.Should().Be(PseudonymizationStep.FailureReason);
        row.RawTranscript.Should().BeNull();
        row.TranscriptPseudonymized.Should().BeFalse();
        row.StructureStatus.Should().BeNull();
        row.PseudonymizationFailedAt.Should().Be(Now);
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.NoteId == note && a.Action == PseudonymizationStep.FailedAction, Ct);
        audit.NewValue.Should().BeNull();
        logger.Messages.Should().Equal($"Pseudonymization failed for note {note}: {nameof(InvalidOperationException)}");
        (await PseudonymizationScratch.NameAppearsAnywhereAsync(s, Secret)).Should().BeFalse();
        holds.IsHeld(note, provider).Should().BeTrue();
        (await NoteQueries.GetAsync(db, provider, note, Ct, holds))!.PseudonymizationRetryable.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ModelRunOverItsBudget_FailsTheNote()
    {
        // Arrange: the real detector turns a run over RunTimeout into a TimeoutException (GatedAsync).
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct);
        var holds = new PseudonymizationHolds(new FakeTimeProvider(Now));
        var step = PseudonymizationKit.Step(new FakeThirdPartyDetector(fail: new TimeoutException()), new FakeTimeProvider(Now), holds: holds);
        await using var db = s.CreateDbContext();

        // Act
        var outcome = await step.RunAsync(db, note, provider, patient, "Joana ligou.", Ct);

        // Assert
        outcome.Should().Be(PseudonymizationOutcome.Failed);
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct)).FailureReason.Should().Be(PseudonymizationStep.FailureReason);
        holds.IsHeld(note, provider).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_LongTranscript_IsDetectedChunkByChunk_AndSucceedsAlthoughTheWholeTakesLongerThanOneRun()
    {
        // Arrange: 40 chunks of ~500 characters, 50 ms per call (2 s in all); names spread over the text.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct);
        var sentences = Enumerable.Range(1, 800).Select(i => i == 10 ? "Joana ligou de manhã." : i == 790 ? "Depois a Joana voltou." : $"Relato da parte {i}.");
        var text = string.Join(" ", sentences);
        var detector = new FakeThirdPartyDetector(chunk => chunk.Contains("Joana") ? [("Joana", P)] : []) { Delay = TimeSpan.FromMilliseconds(50) };
        var step = new PseudonymizationStep(PseudonymizationKit.Pseudonymizer(detector, new FakeTimeProvider(Now), chunkChars: 500),
            new PseudonymizationHolds(new FakeTimeProvider(Now)), new FakeTimeProvider(Now), NullLogger<PseudonymizationStep>.Instance);
        await using var db = s.CreateDbContext();

        // Act
        var outcome = await step.RunAsync(db, note, provider, patient, text, Ct);

        // Assert
        outcome.Should().Be(PseudonymizationOutcome.Done);
        detector.Calls.Should().BeGreaterThan(20);
        detector.ChunkLengths.Should().OnlyContain(l => l <= 500);
        var row = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct);
        row.RawTranscript.Should().NotContain("Joana").And.Contain("[P1] ligou de manhã.").And.Contain("Depois a [P1] voltou.");
    }

    [Fact]
    public async Task Restructure_OnANoteFailedByPseudonymization_IsRefused()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct);
        await using var db = s.CreateDbContext();
        await PseudonymizationKit.Step(new FakeThirdPartyDetector(fail: new FormatException()), new FakeTimeProvider(Now))
            .RunAsync(db, note, provider, patient, "Joana ligou.", Ct);
        var queue = new TranscriptionQueue();

        // Act
        var outcome = await NoteRestructure.RequestAsync(db, queue, provider, note, "DAP", Now, Ct);

        // Assert
        outcome.Should().Be(RestructureOutcome.Conflict);
        queue.Reader.TryRead(out _).Should().BeFalse();
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct)).RawTranscript.Should().BeNull();
    }

    [Fact]
    public async Task LiveNoteDeletedDuringTheStep_IsGone_AndRecordsTheAudioWithoutAFailure()
    {
        // Arrange: the note is no longer Processing (deleted or discarded meanwhile).
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var recording = Guid.NewGuid();
        var note = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct, "VoiceLive", recording);
        await s.ExecuteAsync("""UPDATE "Notes" SET "Status" = 'ReadyForReview' WHERE "Id" = @n""", Ct, ("n", note));
        var meta = new RecordingMeta(provider, patient, null, "DAP", "pt", Now.AddMinutes(-10), LiveRecordings.DictationMode, [RecordingSources.InPerson]);
        byte[] bytes = [1, 0, 2, 0];
        await using var db = s.CreateDbContext();

        // Act
        var outcome = await NoteTranscription.ProcessAsync(db, new FakeTranscriber(_ => "Joana chorou."), PseudonymizationKit.Step(clock: new FakeTimeProvider(Now)),
            new TranscriptionJob(note, provider, "pt", new PcmAudio(bytes, 4), recording, meta), new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(TranscriptionOutcome.Gone);
        bytes.Should().OnlyContain(b => b == 0);
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct)).RawTranscript.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_NoteNoLongerProcessing_IsGone_AndHoldsNothing()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await s.AddNoteAsync(provider, patient, Now, Ct);
        var holds = new PseudonymizationHolds(new FakeTimeProvider(Now));
        var step = PseudonymizationKit.Step(new FakeThirdPartyDetector(fail: new InvalidOperationException()), new FakeTimeProvider(Now), holds: holds);
        await using var db = s.CreateDbContext();

        // Act
        var outcome = await step.RunAsync(db, note, provider, patient, "Joana ligou.", Ct);

        // Assert
        outcome.Should().Be(PseudonymizationOutcome.Gone);
        holds.IsHeld(note, provider).Should().BeFalse();
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct)).Status.Should().Be("ReadyForReview");
    }

    [Fact]
    public async Task Retry_WhileHeld_RunsTheStepAgainThenStructures()
    {
        // Arrange: a failure holds the text; the retry finds a working detector.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct);
        var clock = new FakeTimeProvider(Now);
        var holds = new PseudonymizationHolds(clock);
        await using (var db = s.CreateDbContext())
        {
            await PseudonymizationKit.Step(new FakeThirdPartyDetector(fail: new FormatException()), clock, holds: holds)
                .RunAsync(db, note, provider, patient, "Joana ligou chorando.", Ct);
        }
        clock.Advance(TimeSpan.FromMinutes(3));
        var queue = new TranscriptionQueue();
        string? structured = null;
        var structurer = new FakeNoteStructurer((text, _) =>
        {
            structured = text;
            return [new("data", text), new("assessment", ""), new("plan", "")];
        });
        using var services = Services(s, PseudonymizationKit.Step(FakeThirdPartyDetector.Finding(("Joana", P)), clock, holds: holds), structurer);
        var hub = new RecordingHub();

        // Act
        RetryOutcome outcome;
        await using (var db = s.CreateDbContext())
        {
            outcome = await PseudonymizationRetry.RequestAsync(db, holds, queue, provider, note, clock.GetUtcNow(), Ct);
        }
        queue.Reader.TryRead(out var job).Should().BeTrue();
        await TranscriptionWorker.RunPseudonymizeAsync((PseudonymizeJob)job!, services.GetRequiredService<IServiceScopeFactory>(), hub, clock,
            NullLogger.Instance, Ct);

        // Assert
        outcome.Should().Be(RetryOutcome.Accepted);
        ((PseudonymizeJob)job!).HeldUntil.Should().Be(Now + PseudonymizationHolds.MaxHold);
        holds.IsHeld(note, provider).Should().BeFalse();
        await using var read = s.CreateDbContext();
        var row = await read.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct);
        row.Status.Should().Be("ReadyForReview");
        row.FailureReason.Should().BeNull();
        row.PseudonymizationFailedAt.Should().BeNull();
        row.RawTranscript.Should().Be("[P1] ligou chorando.");
        row.StructureStatus.Should().Be("done");
        structured.Should().Be("[P1] ligou chorando.");
        hub.Sent.Select(m => m.Method).Should().ContainInOrder("NoteReady", "NoteProgress", "NoteStructured");
    }

    [Fact]
    public async Task Retry_AfterTheHoldExpired_IsGone_AndOtherCasesAreRefused()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var other = await s.AddProviderAsync(Ct);
        var failed = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct);
        var ready = await s.AddNoteAsync(provider, patient, Now, Ct);
        var clock = new FakeTimeProvider(Now);
        var holds = new PseudonymizationHolds(clock);
        var queue = new TranscriptionQueue();
        await using var db = s.CreateDbContext();
        await PseudonymizationKit.Step(new FakeThirdPartyDetector(fail: new FormatException()), clock, holds: holds)
            .RunAsync(db, failed, provider, patient, "Joana ligou.", Ct);

        // Act
        var foreign = await PseudonymizationRetry.RequestAsync(db, holds, queue, other, failed, Now, Ct);
        var notFailed = await PseudonymizationRetry.RequestAsync(db, holds, queue, provider, ready, Now, Ct);
        clock.Advance(PseudonymizationHolds.MaxHold + TimeSpan.FromSeconds(1));
        var gone = await PseudonymizationRetry.RequestAsync(db, holds, queue, provider, failed, clock.GetUtcNow(), Ct);

        // Assert
        foreign.Should().Be(RetryOutcome.NotFound);
        notFailed.Should().Be(RetryOutcome.NotFailed);
        gone.Should().Be(RetryOutcome.Gone);
        queue.Reader.TryRead(out _).Should().BeFalse();
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == failed, Ct)).Status.Should().Be("Failed");
        (await NoteQueries.GetAsync(db, provider, failed, Ct, holds))!.PseudonymizationRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task AiModeNote_GoesThroughTheStepBeforeStructuring_ManualNoteDoesNot()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var queue = new TranscriptionQueue();
        var detector = FakeThirdPartyDetector.Finding(("Joana", P));
        string? structured = null;
        var structurer = new FakeNoteStructurer((text, _) =>
        {
            structured = text;
            return [new("data", text), new("assessment", ""), new("plan", "")];
        });
        using var services = Services(s, PseudonymizationKit.Step(detector, new FakeTimeProvider(Now)), structurer);
        CreateNoteResult ai, manual;
        await using (var db = s.CreateDbContext())
        {
            ai = await NoteCreation.CreateAsync(db, queue, provider, new CreateNoteRequest(patient, null, "DAP", "ai", "Joana ligou chorando."), Now, Ct);
        }
        await using (var db = s.CreateDbContext())
        {
            manual = await NoteCreation.CreateAsync(db, queue, provider, new CreateNoteRequest(patient, null, "DAP", "manual", "Joana ligou."), Now, Ct);
        }

        // Act
        queue.Reader.TryRead(out var job).Should().BeTrue();
        await TranscriptionWorker.RunPseudonymizeAsync((PseudonymizeJob)job!, services.GetRequiredService<IServiceScopeFactory>(), new RecordingHub(),
            new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        queue.Reader.TryRead(out _).Should().BeFalse();
        detector.Calls.Should().Be(1);
        await using var read = s.CreateDbContext();
        var aiNote = await read.Notes.AsNoTracking().SingleAsync(n => n.Id == ai.NoteId, Ct);
        aiNote.RawTranscript.Should().Be("[P1] ligou chorando.");
        aiNote.TranscriptPseudonymized.Should().BeTrue();
        aiNote.Status.Should().Be("ReadyForReview");
        aiNote.StructureStatus.Should().Be("done");
        structured.Should().Be("[P1] ligou chorando.");
        var manualNote = await read.Notes.AsNoTracking().SingleAsync(n => n.Id == manual.NoteId, Ct);
        manualNote.RawTranscript.Should().BeNull();
        manualNote.TranscriptPseudonymized.Should().BeFalse();
        (await PseudonymizationScratch.NameAppearsAnywhereAsync(s, "Joana")).Should().BeFalse();
    }

    [Fact]
    public void Validate_ManualModeWithText_IsRefused()
    {
        // Arrange
        var request = new CreateNoteRequest(Guid.NewGuid(), null, "DAP", "manual", "Joana ligou.");

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey("text");
    }

    [Fact]
    public async Task Retry_WhenTheDatabaseWriteFails_KeepsTheHeldTextWithItsDeadline()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct);
        var clock = new FakeTimeProvider(Now);
        var holds = new PseudonymizationHolds(clock);
        await using (var first = s.CreateDbContext())
        {
            await PseudonymizationKit.Step(new FakeThirdPartyDetector(fail: new FormatException()), clock, holds: holds)
                .RunAsync(first, note, provider, patient, "Joana ligou.", Ct);
        }
        clock.Advance(TimeSpan.FromMinutes(2));
        var queue = new TranscriptionQueue();
        await using var db = s.CreateDbContext();
        // The scratch database refuses every update of a note, as an unavailable database would.
        await db.Database.ExecuteSqlRawAsync(
            "CREATE FUNCTION refuse_note_update() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'refused'; END $$ LANGUAGE plpgsql; "
            + "CREATE TRIGGER refuse_note_update BEFORE UPDATE ON \"Notes\" FOR EACH ROW EXECUTE FUNCTION refuse_note_update();", Ct);

        // Act
        Func<Task> act = () => PseudonymizationRetry.RequestAsync(db, holds, queue, provider, note, clock.GetUtcNow(), Ct);
        await act.Should().ThrowAsync<Exception>();

        // Assert
        holds.IsHeld(note, provider).Should().BeTrue();
        queue.Reader.TryRead(out _).Should().BeFalse();
        holds.Take(note, provider)!.Until.Should().Be(Now + PseudonymizationHolds.MaxHold);
    }

    [Fact]
    public void HeldText_ToString_DoesNotPrintTheText()
    {
        // Arrange
        var held = new HeldText(Guid.NewGuid(), "Joana ligou chorando.", Now);

        // Act
        var text = held.ToString();

        // Assert
        text.Should().NotContain("Joana");
        text.Should().Be("HeldText");
    }

    [Fact]
    public async Task LiveTranscript_IsPseudonymisedBeforeItIsWritten_AndAFailureKeepsTheAudioRecordAndWritesNoName()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var okRecording = Guid.NewGuid();
        var failRecording = Guid.NewGuid();
        var ok = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct, "VoiceLive", okRecording);
        var failing = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct, "VoiceLive", failRecording);
        var meta = new RecordingMeta(provider, patient, null, "DAP", "pt", Now.AddMinutes(-10), LiveRecordings.DictationMode, [RecordingSources.InPerson]);
        var holds = new PseudonymizationHolds(new FakeTimeProvider(Now));
        var good = PseudonymizationKit.Step(FakeThirdPartyDetector.Finding(("Joana", P)), new FakeTimeProvider(Now), holds: holds);
        var bad = PseudonymizationKit.Step(new FakeThirdPartyDetector(fail: new InvalidOperationException()), new FakeTimeProvider(Now), holds: holds);
        byte[] okBytes = [1, 0, 2, 0];
        byte[] failBytes = [1, 0, 2, 0];
        var transcriber = new FakeTranscriber(_ => $"Joana {Secret} chorou.");

        // Act
        TranscriptionOutcome okOutcome, failOutcome;
        await using (var db = s.CreateDbContext())
        {
            okOutcome = await NoteTranscription.ProcessAsync(db, transcriber, good,
                new TranscriptionJob(ok, provider, "pt", new PcmAudio(okBytes, 4), okRecording, meta), new FakeTimeProvider(Now), NullLogger.Instance, Ct);
        }
        await using (var db = s.CreateDbContext())
        {
            failOutcome = await NoteTranscription.ProcessAsync(db, transcriber, bad,
                new TranscriptionJob(failing, provider, "pt", new PcmAudio(failBytes, 4), failRecording, meta), new FakeTimeProvider(Now), NullLogger.Instance, Ct);
        }

        // Assert
        okOutcome.Should().Be(TranscriptionOutcome.Ready);
        failOutcome.Should().Be(TranscriptionOutcome.PseudonymizationFailed);
        okBytes.Should().OnlyContain(b => b == 0);
        failBytes.Should().OnlyContain(b => b == 0);
        await using var read = s.CreateDbContext();
        // The detector found only the first name: the surname after it goes into the same token (fix round 1).
        (await read.Notes.AsNoTracking().SingleAsync(n => n.Id == ok, Ct)).RawTranscript.Should().Be("[P1] chorou.");
        (await PseudonymizationScratch.NameAppearsAnywhereAsync(s, Secret)).Should().BeFalse();
        var failedNote = await read.Notes.AsNoTracking().SingleAsync(n => n.Id == failing, Ct);
        failedNote.Status.Should().Be("Failed");
        failedNote.FailureReason.Should().Be(PseudonymizationStep.FailureReason);
        failedNote.RawTranscript.Should().BeNull();
        holds.IsHeld(failing, provider).Should().BeTrue();
        var outcomes = await read.AudioDestructionLogs.AsNoTracking().Where(l => l.PatientId == patient).Select(l => l.Outcome).ToListAsync(Ct);
        outcomes.Should().BeEquivalentTo([AudioDestruction.Transcribed, AudioDestruction.Transcribed]);
    }

    [Fact]
    public async Task FailOrphans_AiNoteLeftInProcessingByARestart_FailsWithServerRestart()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await PseudonymizationScratch.ProcessingNoteAsync(s, provider, patient, Now, Ct);
        await using var db = s.CreateDbContext();

        // Act
        await LiveRecordings.FailOrphansAsync(db, Now, Ct);

        // Assert
        var row = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct);
        row.Status.Should().Be("Failed");
        row.FailureReason.Should().Be("server_restart");
        row.StructureStatus.Should().BeNull();
    }

    [Fact]
    public async Task RetryEndpoint_WithoutSession_Returns401()
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = new Uri("http://localhost:5273/") };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        // Act
        using var response = await client.PostAsync($"/api/phi/providers/me/notes/{Guid.NewGuid()}/pseudonymization/retry", null, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<(Guid Provider, Guid Patient)> SeedAsync(RetentionScratch s)
    {
        var provider = await s.AddProviderAsync(Ct);
        return (provider, await s.AddPatientAsync(provider, "Ana", Ct));
    }

    private static ServiceProvider Services(RetentionScratch s, PseudonymizationStep step, INoteStructurer structurer) =>
        new ServiceCollection()
            .AddScoped(_ => s.CreateDbContext())
            .AddSingleton(step)
            .AddSingleton(structurer)
            .AddSingleton<IThemeSuggester>(new FakeThemeSuggester())
            .BuildServiceProvider();
}
