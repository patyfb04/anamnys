using Anamnys.Server.Data.Entities;
using Anamnys.Server.Integrity;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using QuestPDF.Infrastructure;
using static Anamnys.Tests.Integrity.IntegrityTestData;

namespace Anamnys.Tests.Integrity;

// Sealing the records signed before F-45 (design/specs/2026-11-03-integrity-chain-design.md §1, §4, §10 "Backfill").
[Collection(SharedAppHostCollection.Name)]
public class IntegrityBackfillTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static IntegrityBackfillTests() => QuestPDF.Settings.License = LicenseType.Community;

    [Fact]
    public async Task RunAsync_SealsSignedRecordsInSigningOrder_FlaggedRetroactive_AtTheRunTime()
    {
        // Arrange: legacy notes and a document signed out of insertion order, a tie on SignedAt, a draft.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var t1 = new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddDays(10);
        var late = await s.AddNoteAsync(provider, patient, t1, Ct, signedAt: t2.AddDays(5));
        var document = await s.AddSignedDocumentAsync(provider, patient, t1, Ct);
        var tieA = await s.AddNoteAsync(provider, patient, t1, Ct, signedAt: t2);
        var tieB = await s.AddNoteAsync(provider, patient, t1, Ct, signedAt: t2);
        await s.AddSectionAsync(tieA, Ct);
        await AddReadyNoteAsync(s, provider, patient, Ct);
        var ties = new[] { tieA, tieB }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        await using var db = s.CreateDbContext();

        // Act
        var created = await IntegrityBackfill.RunAsync(db, Now, Ct, provider);

        // Assert
        created.Should().Be(new IntegrityBackfill.Result(4, 0));
        var seals = await SealsAsync(s, provider, Ct);
        seals.Select(x => x.SubjectId).Should().Equal(document, ties[0], ties[1], late);
        seals.Select(x => x.SubjectType).Should().Equal(IntegritySeal.Document, IntegritySeal.Note, IntegritySeal.Note, IntegritySeal.Note);
        seals.Select(x => x.Sequence).Should().Equal(1, 2, 3, 4);
        seals.Should().OnlyContain(x => x.Retroactive && x.SealedAt == Now && x.DiscardedAt == null);
        for (var i = 0; i < seals.Count; i++)
        {
            (await IntegrityVerifier.VerifySubjectAsync(db, provider, seals[i].SubjectType, seals[i].SubjectId, Tsa, Ct))!
                .Should().Be(new SubjectIntegrity(SubjectStatus.Intact, Now, true, false));
        }
        (await IntegrityVerifier.VerifyChainAsync(db, provider, Tsa, Now, Ct)).FirstProblem.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_SecondRun_AddsNothing_AndAppendsAfterLiveSeals()
    {
        // Arrange: one live seal already in the chain, one legacy note.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var other = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var live = await SignedNoteAsync(s, provider, patient, Now, Ct);
        var legacy = await s.AddNoteAsync(provider, patient, Now, Ct, signedAt: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var otherLegacy = await s.AddNoteAsync(other, await s.AddPatientAsync(other, "Bia", Ct), Now, Ct, signedAt: Now);
        await using var db = s.CreateDbContext();

        // Act
        var first = await IntegrityBackfill.RunAsync(db, Now.AddHours(1), Ct, provider);
        var second = await IntegrityBackfill.RunAsync(db, Now.AddHours(2), Ct, provider);

        // Assert
        first.Created.Should().Be(1);
        second.Created.Should().Be(0);
        var seals = await SealsAsync(s, provider, Ct);
        seals.Select(x => (x.SubjectId, x.Retroactive)).Should().Equal((live, false), (legacy, true));
        seals[1].PreviousHash.Should().Be(seals[0].Hash);
        seals[1].SealedAt.Should().Be(Now.AddHours(1));
        (await SealsAsync(s, other, Ct)).Should().BeEmpty("the run was narrowed to one provider");
        (await IntegrityBackfill.RunAsync(db, Now.AddHours(3), Ct)).Created.Should().Be(1);
        (await SealsAsync(s, other, Ct)).Single().SubjectId.Should().Be(otherLegacy);
    }

    [Fact]
    public async Task RunAsync_WhenOneProviderFails_LogsTheTypeOnly_AndStillSealsTheNext()
    {
        // Arrange: two providers with a legacy note each; a scratch-only trigger makes sealing throw for the provider
        // processed first (providers run in uuid order).
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var a = await s.AddProviderAsync(Ct, withCrp: true);
        var b = await s.AddProviderAsync(Ct, withCrp: true);
        var ordered = new[] { a, b }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        var (failing, healthy) = (ordered[0], ordered[1]);
        await s.AddNoteAsync(failing, await s.AddPatientAsync(failing, "Ana", Ct), Now, Ct, signedAt: Now);
        var healthyNote = await s.AddNoteAsync(healthy, await s.AddPatientAsync(healthy, "Bia", Ct), Now, Ct, signedAt: Now);
        await s.ExecuteAsync(
            $$"""
            CREATE FUNCTION test_fail_seal() RETURNS trigger LANGUAGE plpgsql AS $f$
            BEGIN RAISE EXCEPTION 'sealing refused for test'; END $f$;
            CREATE TRIGGER test_fail_seal BEFORE INSERT ON "IntegritySeals"
                FOR EACH ROW WHEN (NEW."ProviderId" = '{{failing:D}}'::uuid) EXECUTE FUNCTION test_fail_seal();
            """, Ct);
        var logger = new RecordingLogger();
        await using var db = s.CreateDbContext();

        // Act
        var result = await IntegrityBackfill.RunAsync(db, Now, Ct, logger: logger);

        // Assert
        result.Should().Be(new IntegrityBackfill.Result(1, 1));
        (await SealsAsync(s, failing, Ct)).Should().BeEmpty("the failing provider's transaction rolled back");
        (await SealsAsync(s, healthy, Ct)).Single().SubjectId.Should().Be(healthyNote);
        logger.Messages.Should().ContainSingle()
            .Which.Should().Be("Retroactive sealing failed for one provider: DbUpdateException");
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            exception.Should().BeNull("no exception (and so no message text) is handed to the logger");
            Messages.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task SchemaPresentAsync_OnTheBootstrapSchema_IsTrue()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();

        // Act / Assert
        (await IntegrityBackfill.SchemaPresentAsync(db, Ct)).Should().BeTrue();
    }
}
