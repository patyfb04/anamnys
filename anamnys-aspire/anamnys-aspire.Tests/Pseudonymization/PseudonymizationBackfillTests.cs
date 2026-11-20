using System.Net;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Integrity;
using Anamnys.Server.Pseudonymization;
using Anamnys.Tests.Auth;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static Anamnys.Tests.Integrity.IntegrityTestData;

namespace Anamnys.Tests.Pseudonymization;

// Backfill of legacy transcripts (design/specs/2026-11-17-pseudonymization-design.md §6, §8): converts unconverted notes
// through the pseudonymiser, never touches sections or seals, resumes after a restart, counts failures and retries them
// on the next start, and is scoped to the provider. Scratch database; fake detector.
[Collection(SharedAppHostCollection.Name)]
public class PseudonymizationBackfillTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 11, 19, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string P = EntityKinds.Person;
    private const string Secret = "Segredo";
    private static readonly Microsoft.AspNetCore.DataProtection.IDataProtectionProvider Protection =
        new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();

    [Fact]
    public async Task RunPending_ConvertsUnconvertedNotes_LeavesTheRestAndAuditsCountsOnly()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var legacy = await LegacyNoteAsync(s, provider, patient, $"Joana {Secret} ligou.");
        var second = await LegacyNoteAsync(s, provider, patient, $"Joana {Secret}, de novo.");
        var empty = await LegacyNoteAsync(s, provider, patient, "");
        var converted = await LegacyNoteAsync(s, provider, patient, "[P1] ligou.", pseudonymized: true);
        var worker = Worker(s, FakeThirdPartyDetector.Finding(($"Joana {Secret}", P)));
        await StartAsync(s, provider);

        // Act
        await worker.RunPendingAsync(Ct);

        // Assert
        await using var db = s.CreateDbContext();
        var rows = await db.Notes.AsNoTracking().Where(n => n.ProviderId == provider).ToDictionaryAsync(n => n.Id, Ct);
        rows[legacy].RawTranscript.Should().Be("[P1] ligou.");
        rows[legacy].TranscriptPseudonymized.Should().BeTrue();
        rows[second].RawTranscript.Should().Be("[P1], de novo.");
        rows[empty].TranscriptPseudonymized.Should().BeFalse();
        rows[converted].RawTranscript.Should().Be("[P1] ligou.");
        rows.Values.Select(n => n.Status).Should().OnlyContain(st => st == "ReadyForReview");
        var audits = await db.AuditEntries.AsNoTracking().Where(a => a.Action == PseudonymizationStep.DoneAction).ToListAsync(Ct);
        audits.Select(a => a.NoteId).Should().BeEquivalentTo([legacy, second]);
        audits.Should().OnlyContain(a => a.NewValue == "person=1,organization=0,school=0,address=0");
        var started = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == PseudonymizationBackfills.StartedAction, Ct);
        started.ActorId.Should().Be(provider);
        started.NewValue.Should().Be("total=2");
        var status = await PseudonymizationBackfills.StatusAsync(db, provider, Ct);
        status.Should().Be(new BackfillStatus(0, false, 2, 2, 0, At, At));
        (await PseudonymizationScratch.NameAppearsAnywhereAsync(s, Secret)).Should().BeFalse();
    }

    [Fact]
    public async Task RunPending_SignedNote_KeepsSectionsAndTheIntegrityChainVerifying()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var note = await SignedNoteAsync(s, provider, patient, Now, Ct);
        await s.ExecuteAsync("""UPDATE "Notes" SET "RawTranscript" = @t WHERE "Id" = @n""", Ct, ("n", note), ("t", $"Joana {Secret} ligou."));
        async Task<List<string>> SectionRowsAsync()
        {
            await using var d = s.CreateDbContext();
            return await d.NoteSections.AsNoTracking().Where(x => x.NoteId == note).OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Content).ToListAsync(Ct);
        }
        var sectionsBefore = await SectionRowsAsync();
        var sealsBefore = await SealsAsync(s, provider, Ct);
        var worker = Worker(s, FakeThirdPartyDetector.Finding(($"Joana {Secret}", P)));
        await StartAsync(s, provider);

        // Act
        await worker.RunPendingAsync(Ct);

        // Assert
        await using var db = s.CreateDbContext();
        var row = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct);
        row.RawTranscript.Should().Be("[P1] ligou.");
        row.TranscriptPseudonymized.Should().BeTrue();
        row.Status.Should().Be("Signed");
        (await SectionRowsAsync()).Should().Equal(sectionsBefore);
        (await SealsAsync(s, provider, Ct)).Should().BeEquivalentTo(sealsBefore);
        (await IntegrityVerifier.VerifyChainAsync(db, provider, Tsa, Now, Ct)).FirstProblem.Should().BeNull();
        (await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, note, Tsa, Ct))!.Status.Should().Be(SubjectStatus.Intact);
    }

    [Fact]
    public async Task RunPending_ARunningBackfill_ResumesWithANewWorkerAfterARestart()
    {
        // Arrange: the first worker only got as far as the start (the process died); the second is a new instance.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await LegacyNoteAsync(s, provider, patient, $"Joana {Secret} ligou.");
        await StartAsync(s, provider);
        var restarted = Worker(s, FakeThirdPartyDetector.Finding(($"Joana {Secret}", P)));

        // Act
        await restarted.RunPendingAsync(Ct);

        // Assert
        await using var db = s.CreateDbContext();
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct)).RawTranscript.Should().Be("[P1] ligou.");
        (await PseudonymizationBackfills.StatusAsync(db, provider, Ct)).Should().Be(new BackfillStatus(0, false, 1, 1, 0, At, At));
    }

    [Fact]
    public async Task RunPending_ARestartMidRun_ContinuesWithoutRecountingTheConvertedNotes()
    {
        // Arrange: one of two notes was converted before the restart (Done = 1, row still running).
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        await LegacyNoteAsync(s, provider, patient, $"Joana {Secret} ligou.");
        await LegacyNoteAsync(s, provider, patient, $"Joana {Secret} voltou.");
        await StartAsync(s, provider);
        await Worker(s, FakeThirdPartyDetector.Finding(($"Joana {Secret}", P))).RunPendingAsync(Ct);
        var again = await LegacyNoteAsync(s, provider, patient, $"Joana {Secret} saiu.");
        await s.ExecuteAsync("""UPDATE "PseudonymizationBackfills" SET "FinishedAt" = NULL WHERE "ProviderId" = @p""", Ct, ("p", provider));

        // Act
        await Worker(s, FakeThirdPartyDetector.Finding(($"Joana {Secret}", P))).RunPendingAsync(Ct);

        // Assert
        await using var db = s.CreateDbContext();
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == again, Ct)).RawTranscript.Should().Be("[P1] saiu.");
        (await PseudonymizationBackfills.StatusAsync(db, provider, Ct)).Should().Be(new BackfillStatus(0, false, 3, 3, 0, At, At));
    }

    [Fact]
    public async Task RunPending_DetectionFails_NoteStaysUnconverted_IsCounted_AndRetriedOnTheNextStart()
    {
        // Arrange: the detector fails for the transcript containing "Falha".
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var good = await LegacyNoteAsync(s, provider, patient, $"Joana {Secret} ligou.");
        var bad = await LegacyNoteAsync(s, provider, patient, $"Falha com Joana {Secret}.");
        await StartAsync(s, provider);
        var flaky = new FakeThirdPartyDetector(text => text.Contains("Falha") ? throw new InvalidOperationException($"Joana {Secret}") : [($"Joana {Secret}", P)]);

        // Act
        await Worker(s, flaky).RunPendingAsync(Ct);

        // Assert
        await using (var db = s.CreateDbContext())
        {
            var rows = await db.Notes.AsNoTracking().Where(n => n.ProviderId == provider).ToDictionaryAsync(n => n.Id, Ct);
            rows[good].TranscriptPseudonymized.Should().BeTrue();
            rows[bad].TranscriptPseudonymized.Should().BeFalse();
            rows[bad].RawTranscript.Should().Be($"Falha com Joana {Secret}.");
            (await PseudonymizationBackfills.StatusAsync(db, provider, Ct)).Should().Be(new BackfillStatus(1, false, 2, 1, 1, At, At));
        }

        // Act: the next start, with a detector that works.
        await StartAsync(s, provider);
        await Worker(s, FakeThirdPartyDetector.Finding(($"Joana {Secret}", P))).RunPendingAsync(Ct);

        // Assert
        await using var after = s.CreateDbContext();
        (await after.Notes.AsNoTracking().SingleAsync(n => n.Id == bad, Ct)).RawTranscript.Should().Be("Falha com [P1].");
        (await PseudonymizationBackfills.StatusAsync(after, provider, Ct)).Should().Be(new BackfillStatus(0, false, 1, 1, 0, At, At));
    }

    [Fact]
    public async Task RunPending_NoteConvertedBetweenReadAndWrite_IsNotWrittenAgain_AndLeavesTheTotalOneLower()
    {
        // Arrange: while the detector runs, someone else converts the note (its text and flag change under the backfill).
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await LegacyNoteAsync(s, provider, patient, $"Joana {Secret} ligou.");
        await StartAsync(s, provider);
        var racing = new HookDetector(async () => await s.ExecuteAsync(
            """UPDATE "Notes" SET "RawTranscript" = '[P1] ligou.', "TranscriptPseudonymized" = true WHERE "Id" = @n""", Ct, ("n", note)),
            FakeThirdPartyDetector.Finding(($"Joana {Secret}", P)));

        // Act
        await Worker(s, racing).RunPendingAsync(Ct);

        // Assert: the other conversion stands untouched, nothing audited, and the status adds up.
        await using var db = s.CreateDbContext();
        var row = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct);
        row.RawTranscript.Should().Be("[P1] ligou.");
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.Action == PseudonymizationStep.DoneAction, Ct)).Should().Be(0);
        (await PseudonymizationBackfills.StatusAsync(db, provider, Ct)).Should().Be(new BackfillStatus(0, false, 0, 0, 0, At, At));
    }

    [Fact]
    public async Task RunPending_SecondWorkerConvertsTheSameNoteMeanwhile_LeavesExactlyOneConversion()
    {
        // Arrange: while the first worker's detector runs, a second worker (another instance) converts the note.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await LegacyNoteAsync(s, provider, patient, $"Joana {Secret} ligou.");
        await StartAsync(s, provider);
        var second = Worker(s, FakeThirdPartyDetector.Finding(($"Joana {Secret}", P)));
        var first = Worker(s, new HookDetector(() => second.RunPendingAsync(Ct), FakeThirdPartyDetector.Finding(($"Joana {Secret}", P))));

        // Act
        await first.RunPendingAsync(Ct);

        // Assert: pseudonymised once (not "[P1]" of "[P1]"), one audit entry, one Done.
        await using var db = s.CreateDbContext();
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == note, Ct)).RawTranscript.Should().Be("[P1] ligou.");
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.NoteId == note && a.Action == PseudonymizationStep.DoneAction, Ct)).Should().Be(1);
        (await PseudonymizationBackfills.StatusAsync(db, provider, Ct)).Should().Be(new BackfillStatus(0, false, 1, 1, 0, At, At));
    }

    [Fact]
    public void BackfillDetector_RunsAtBackgroundPriority_AndLiveDetectorAtInteractive()
    {
        // Arrange
        var gate = new Anamnys.Server.NoteStructuring.LlmGate();
        var options = new Anamnys.Server.NoteStructuring.NoteStructuringOptions();
        Task<(LLama.LLamaWeights Weights, LLama.Common.ModelParams Params)> Load(CancellationToken _) => throw new InvalidOperationException();

        // Act
        var backfill = PseudonymizationSetup.BackfillDetector(Load, options, gate);
        var live = new LlamaThirdPartyDetector(Load, options, gate);

        // Assert
        backfill.Priority.Should().Be(Anamnys.Server.NoteStructuring.LlmPriority.Background);
        live.Priority.Should().Be(Anamnys.Server.NoteStructuring.LlmPriority.Interactive);
    }

    [Fact]
    public async Task RunPending_FailureLogs_CarryIdsAndTypeNamesOnly()
    {
        // Arrange: the exception message echoes the name.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var note = await LegacyNoteAsync(s, provider, patient, $"Joana {Secret} ligou.");
        await StartAsync(s, provider);
        var logger = new CapturingLogger<BackfillWorker>();
        var worker = Worker(s, new FakeThirdPartyDetector(fail: new InvalidOperationException($"Joana {Secret}")), logger);

        // Act
        await worker.RunPendingAsync(Ct);

        // Assert
        logger.Messages.Should().Equal($"Pseudonymization backfill failed for note {note}: {nameof(InvalidOperationException)}");
        await using var db = s.CreateDbContext();
        (await db.AuditEntries.AsNoTracking().Where(a => a.Action == PseudonymizationStep.DoneAction).CountAsync(Ct)).Should().Be(0);
        (await db.AuditEntries.AsNoTracking().Select(a => a.NewValue + "|" + a.PreviousValue).ToListAsync(Ct)).Should().NotContain(v => v.Contains(Secret));
    }

    [Fact]
    public async Task Start_WhileRunning_ReturnsFalse_AndAfterItFinishedStartsAgain()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        await LegacyNoteAsync(s, provider, patient, $"Joana {Secret} ligou.");
        await using var db = s.CreateDbContext();

        // Act
        var first = await PseudonymizationBackfills.TryStartAsync(db, provider, At, Ct);
        var second = await PseudonymizationBackfills.TryStartAsync(db, provider, At, Ct);
        var duringRun = await PseudonymizationBackfills.StatusAsync(db, provider, Ct);
        await Worker(s, FakeThirdPartyDetector.Finding(($"Joana {Secret}", P))).RunPendingAsync(Ct);
        var third = await PseudonymizationBackfills.TryStartAsync(db, provider, At.AddHours(1), Ct);

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
        duringRun.Should().Be(new BackfillStatus(1, true, 1, 0, 0, At, null));
        third.Should().BeTrue();
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.Action == PseudonymizationBackfills.StartedAction, Ct)).Should().Be(2);
    }

    [Fact]
    public async Task Backfill_IsScopedToTheProvider()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        var (other, otherPatient) = await SeedAsync(s);
        var mine = await LegacyNoteAsync(s, provider, patient, $"Joana {Secret} ligou.");
        var theirs = await LegacyNoteAsync(s, other, otherPatient, $"Joana {Secret} ligou.");
        await StartAsync(s, provider);

        // Act
        await Worker(s, FakeThirdPartyDetector.Finding(($"Joana {Secret}", P))).RunPendingAsync(Ct);

        // Assert
        await using var db = s.CreateDbContext();
        (await db.Notes.AsNoTracking().SingleAsync(n => n.Id == mine, Ct)).TranscriptPseudonymized.Should().BeTrue();
        var foreign = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == theirs, Ct);
        foreign.TranscriptPseudonymized.Should().BeFalse();
        foreign.RawTranscript.Should().Be($"Joana {Secret} ligou.");
        (await PseudonymizationBackfills.StatusAsync(db, other, Ct)).Should().Be(new BackfillStatus(1, false, 0, 0, 0, null, null));
        (await db.PseudonymizationBackfills.AsNoTracking().CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Status_BeforeAnyStart_CountsOnlyNotesWithATranscriptStillUnconverted()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var (provider, patient) = await SeedAsync(s);
        await LegacyNoteAsync(s, provider, patient, "um");
        await LegacyNoteAsync(s, provider, patient, "dois");
        await LegacyNoteAsync(s, provider, patient, "");
        await LegacyNoteAsync(s, provider, patient, "[P1]", pseudonymized: true);
        await s.AddNoteAsync(provider, patient, At, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var status = await PseudonymizationBackfills.StatusAsync(db, provider, Ct);

        // Assert
        status.Should().Be(new BackfillStatus(2, false, 0, 0, 0, null, null));
    }

    [Fact]
    public async Task Endpoints_WithoutSession_Return401()
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = new Uri("http://localhost:5273/") };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        // Act
        using var get = await client.GetAsync("/api/phi/providers/me/pseudonymization/backfill", Ct);
        using var post = await client.PostAsync("/api/phi/providers/me/pseudonymization/backfill", null, Ct);

        // Assert
        get.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        post.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // Runs a hook (another writer) while detection is in progress, then answers as the inner detector.
    private sealed class HookDetector(Func<Task> hook, IThirdPartyDetector inner) : IThirdPartyDetector
    {
        public async Task<IReadOnlyList<DetectedSpan>> DetectAsync(string text, IReadOnlyCollection<string> exclusions, CancellationToken ct)
        {
            await hook();
            return await inner.DetectAsync(text, exclusions, ct);
        }
    }

    private static async Task<(Guid Provider, Guid Patient)> SeedAsync(RetentionScratch s)
    {
        var provider = await s.AddProviderAsync(Ct);
        return (provider, await s.AddPatientAsync(provider, "Ana", Ct));
    }

    private static async Task<Guid> LegacyNoteAsync(RetentionScratch s, Guid provider, Guid patient, string transcript, bool pseudonymized = false)
    {
        var note = await s.AddNoteAsync(provider, patient, At, Ct);
        await s.ExecuteAsync("""UPDATE "Notes" SET "RawTranscript" = @t, "TranscriptPseudonymized" = @p WHERE "Id" = @n""", Ct,
            ("n", note), ("t", transcript), ("p", pseudonymized));
        return note;
    }

    private static async Task StartAsync(RetentionScratch s, Guid provider)
    {
        await using var db = s.CreateDbContext();
        (await PseudonymizationBackfills.TryStartAsync(db, provider, At, Ct)).Should().BeTrue();
    }

    private static BackfillWorker Worker(RetentionScratch s, IThirdPartyDetector detector, CapturingLogger<BackfillWorker>? logger = null)
    {
        var scopes = new ServiceCollection().AddScoped(_ => s.CreateDbContext()).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var clock = new FakeTimeProvider(At);
        // One key ring for every worker of the process: a restarted worker must read the secrets the earlier one wrote.
        return new BackfillWorker(scopes, PseudonymizationKit.Pseudonymizer(detector, clock, Protection), clock,
            logger ?? new CapturingLogger<BackfillWorker>());
    }
}
