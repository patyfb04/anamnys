using Anamnys.Server.Data.Entities;
using Anamnys.Server.Integrity;
using Anamnys.Server.Retention;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;
using static Anamnys.Tests.Integrity.IntegrityTestData;

namespace Anamnys.Tests.Integrity;

// Verification of one subject and of a whole chain (design/specs/2026-11-03-integrity-chain-design.md §5, §10).
[Collection(SharedAppHostCollection.Name)]
public class IntegrityVerifierTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static IntegrityVerifierTests() => QuestPDF.Settings.License = LicenseType.Community;

    [Fact]
    public async Task VerifySubjectAsync_SignedNoteAndDocument_AreIntactAndNotYetAnchored()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var note = await SignedNoteAsync(s, provider, patient, Now, Ct);
        var document = await SignedDocumentAsync(s, provider, patient, Now.AddMinutes(1), Ct);
        await using var db = s.CreateDbContext();

        // Act
        var noteResult = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, note, Tsa, Ct);
        var documentResult = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Document, document, Tsa, Ct);

        // Assert
        noteResult.Should().Be(new SubjectIntegrity(SubjectStatus.Intact, Now, false, false));
        documentResult.Should().Be(new SubjectIntegrity(SubjectStatus.Intact, Now.AddMinutes(1), false, false));
    }

    [Fact]
    public async Task VerifySubjectAsync_SectionChangedViaSql_ReturnsContentChanged()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var note = await SignedNoteAsync(s, provider, patient, Now, Ct);
        await s.ExecuteAsync("""UPDATE "NoteSections" SET "Content" = 'alterado' WHERE "NoteId" = @n AND "Key" = 'data'""", Ct, ("n", note));
        await using var db = s.CreateDbContext();

        // Act
        var result = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, note, Tsa, Ct);
        var chain = await IntegrityVerifier.VerifyChainAsync(db, provider, Tsa, Now, Ct);

        // Assert
        result!.Status.Should().Be(SubjectStatus.ContentChanged);
        chain.FirstProblem.Should().Be(new ChainProblem(1, IntegritySeal.Note, ChainProblemKind.ContentChanged));
    }

    [Fact]
    public async Task VerifySubjectAsync_StoredHashChanged_ReturnsChainBroken()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var first = await SignedNoteAsync(s, provider, patient, Now, Ct);
        var second = await SignedNoteAsync(s, provider, patient, Now.AddMinutes(1), Ct);
        await TamperSealsAsync(s, """UPDATE "IntegritySeals" SET "Hash" = repeat('f', 64) WHERE "SubjectId" = @n""", Ct, ("n", first));
        await using var db = s.CreateDbContext();

        // Act
        var firstResult = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, first, Tsa, Ct);
        var secondResult = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, second, Tsa, Ct);
        var chain = await IntegrityVerifier.VerifyChainAsync(db, provider, Tsa, Now, Ct);

        // Assert: the changed seal no longer recomputes, and the next one no longer links to it.
        firstResult!.Status.Should().Be(SubjectStatus.ChainBroken);
        secondResult!.Status.Should().Be(SubjectStatus.ChainBroken);
        chain.FirstProblem.Should().Be(new ChainProblem(1, IntegritySeal.Note, ChainProblemKind.ChainBroken));
    }

    [Fact]
    public async Task VerifyChainAsync_RemovedSeal_ShowsTheGap()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await SignedNoteAsync(s, provider, patient, Now, Ct);
        var removed = await SignedNoteAsync(s, provider, patient, Now.AddMinutes(1), Ct);
        await SignedNoteAsync(s, provider, patient, Now.AddMinutes(2), Ct);
        await TamperSealsAsync(s, """DELETE FROM "IntegritySeals" WHERE "SubjectId" = @n""", Ct, ("n", removed));
        await using var db = s.CreateDbContext();

        // Act
        var chain = await IntegrityVerifier.VerifyChainAsync(db, provider, Tsa, Now, Ct);
        var subject = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, removed, Tsa, Ct);

        // Assert
        chain.TotalSeals.Should().Be(2);
        chain.FirstProblem.Should().Be(new ChainProblem(2, null, ChainProblemKind.SequenceGap));
        subject!.Status.Should().Be(SubjectStatus.Unsealed);
    }

    [Fact]
    public async Task VerifyChainAsync_AfterTheDiscard_StaysIntactAndCountsTheDiscarded()
    {
        // Arrange: an expired record (signed note and document) sealed by the backfill, and another patient's note.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var record = await DiscardServiceTests.SeedExpiredAsync(s, store);
        var other = await s.AddPatientAsync(record.Provider, "Bia", Ct);
        var kept = await SignedNoteAsync(s, record.Provider, other, Now, Ct);
        await using (var db = s.CreateDbContext())
        {
            await IntegrityBackfill.RunAsync(db, Now, Ct, record.Provider);
        }

        // Act
        var discard = await DiscardService.DiscardAsync(
            s.CreateDbContext(), store, NullLogger.Instance, record.Provider, record.Patient, "Ana Silva", Now.AddHours(1), Ct);
        await using var check = s.CreateDbContext();
        var chain = await IntegrityVerifier.VerifyChainAsync(check, record.Provider, Tsa, Now, Ct);
        var discardedNote = await IntegrityVerifier.VerifySubjectAsync(check, record.Provider, IntegritySeal.Note, record.Notes[0], Tsa, Ct);
        var keptNote = await IntegrityVerifier.VerifySubjectAsync(check, record.Provider, IntegritySeal.Note, kept, Tsa, Ct);

        // Assert
        discard.Outcome.Should().Be(DiscardOutcome.Ok);
        chain.TotalSeals.Should().Be(3);
        chain.Discarded.Should().Be(2);
        chain.FirstProblem.Should().BeNull();
        discardedNote!.Status.Should().Be(SubjectStatus.Discarded);
        discardedNote.Retroactive.Should().BeTrue();
        keptNote!.Status.Should().Be(SubjectStatus.Intact);
    }

    [Fact]
    public async Task Verify_AnotherProvidersRecords_AreNeverSeen()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var owner = await s.AddProviderAsync(Ct, withCrp: true);
        var stranger = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(owner, "Ana", Ct);
        var note = await SignedNoteAsync(s, owner, patient, Now, Ct);
        var document = await SignedDocumentAsync(s, owner, patient, Now, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var noteResult = await IntegrityVerifier.VerifySubjectAsync(db, stranger, IntegritySeal.Note, note, Tsa, Ct);
        var documentResult = await IntegrityVerifier.VerifySubjectAsync(db, stranger, IntegritySeal.Document, document, Tsa, Ct);
        var chain = await IntegrityVerifier.VerifyChainAsync(db, stranger, Tsa, Now, Ct);

        // Assert
        noteResult.Should().BeNull();
        documentResult.Should().BeNull();
        chain.TotalSeals.Should().Be(0);
        chain.FirstProblem.Should().BeNull();
        chain.LastAnchor.Should().BeNull();
    }

    [Fact]
    public async Task VerifySubjectAsync_SignedWithoutSeal_IsUnsealed_DraftAndUnknownAreNull()
    {
        // Arrange: a note signed outside the signing path (as before F-45), a draft and an unknown id.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var legacy = await s.AddNoteAsync(provider, patient, Now, Ct, signedAt: Now);
        var draft = await AddReadyNoteAsync(s, provider, patient, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var legacyResult = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, legacy, Tsa, Ct);
        var draftResult = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Note, draft, Tsa, Ct);
        var unknown = await IntegrityVerifier.VerifySubjectAsync(db, provider, IntegritySeal.Document, Guid.NewGuid(), Tsa, Ct);
        var wrongType = await IntegrityVerifier.VerifySubjectAsync(db, provider, "patient", legacy, Tsa, Ct);

        // Assert
        legacyResult.Should().Be(new SubjectIntegrity(SubjectStatus.Unsealed, null, false, false));
        draftResult.Should().BeNull();
        unknown.Should().BeNull();
        wrongType.Should().BeNull();
    }

    [Fact]
    public async Task VerifyChainAsync_WalksMoreThanOneBatch()
    {
        // Arrange: a valid chain longer than one batch, built directly.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var signed = await SignedNoteAsync(s, provider, patient, Now, Ct);
        await TamperSealsAsync(s, """UPDATE "IntegritySeals" SET "DiscardedAt" = now() WHERE "SubjectId" = @n""", Ct, ("n", signed));
        var seals = (await SealsAsync(s, provider, Ct)).Single();
        var previous = seals.Hash;
        var count = IntegrityVerifier.BatchSize + 20;
        for (long sequence = 2; sequence <= count; sequence++)
        {
            var subject = Guid.NewGuid();
            var content = new string('c', 64);
            var hash = IntegritySeals.SealHash(previous, sequence, IntegritySeal.Note, subject, content, Now);
            await s.ExecuteAsync(
                """
                INSERT INTO "IntegritySeals" ("ProviderId","Sequence","SubjectType","SubjectId","ContentHash","PreviousHash","Hash","SealedAt","DiscardedAt")
                VALUES (@p, @seq, 'note', @subject, @content, @prev, @hash, @at, @at)
                """, Ct, ("p", provider), ("seq", sequence), ("subject", subject), ("content", content), ("prev", previous), ("hash", hash),
                ("at", Now.UtcDateTime));
            previous = hash;
        }
        await using var db = s.CreateDbContext();

        // Act
        var chain = await IntegrityVerifier.VerifyChainAsync(db, provider, Tsa, Now, Ct);

        // Assert
        chain.TotalSeals.Should().Be(count);
        chain.Discarded.Should().Be(count);
        chain.FirstProblem.Should().BeNull();
        chain.VerifiedAt.Should().Be(Now);
    }
}
