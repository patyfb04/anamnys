using System.Security.Cryptography;
using System.Text;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using Anamnys.Server.Integrity;
using Anamnys.Server.Notes;
using Anamnys.Server.Retention;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;

namespace Anamnys.Tests.Integrity;

// Appending to the chain and the three callers (design/specs/2026-11-03-integrity-chain-design.md §2, §4).
[Collection(SharedAppHostCollection.Name)]
public class IntegritySealTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static IntegritySealTests() => QuestPDF.Settings.License = LicenseType.Community;

    private static readonly Dictionary<string, string> CompleteAtestado = new()
    {
        ["nome_solicitante"] = "Escola X",
        ["finalidade"] = "Afastamento",
        ["descricao_condicoes_psicologicas"] = "Linha um.\nLinha dois.",
        ["prazo_validade"] = "30 dias.",
    };

    private static readonly string[] AtestadoConfirmations = ["informacao_alem_do_solicitado"];

    // A note ready to be signed: DAP format with its three sections.
    private static async Task<Guid> AddReadyNoteAsync(RetentionScratch s, Guid provider, Guid patient)
    {
        var id = Guid.NewGuid();
        await s.ExecuteAsync(
            """
            INSERT INTO "Notes" ("Id","ProviderId","PatientId","Status","InputMode","Format","StructureStatus","RawTranscript")
            VALUES (@id, @p, @pa, 'ReadyForReview', 'VoiceLive', 'DAP', 'done', 'transcricao')
            """, Ct, ("id", id), ("p", provider), ("pa", patient));
        await s.ExecuteAsync(
            """
            INSERT INTO "NoteSections" ("Id","NoteId","Key","Content","Position","GeneratedAt","Revision") VALUES
            (gen_random_uuid(), @id, 'data', 'a', 0, now(), 0),
            (gen_random_uuid(), @id, 'assessment', 'b', 1, now(), 0),
            (gen_random_uuid(), @id, 'plan', 'c', 2, now(), 0)
            """, Ct, ("id", id));
        return id;
    }

    private static async Task<Guid> AddReadyDocumentAsync(RetentionScratch s, Guid provider, Guid patient)
    {
        await using var db = s.CreateDbContext();
        var created = await ClinicalDocumentService.CreateAsync(db, provider, patient, "atestado", null, Now, Ct);
        var id = created.Id!.Value;
        await ClinicalDocumentService.SaveAsync(db, provider, id, CompleteAtestado, AtestadoConfirmations, [], Now, Ct);
        return id;
    }

    private static async Task<string?> NoteHashAsync(RetentionScratch s, Guid provider, Guid id)
    {
        await using var db = s.CreateDbContext();
        return await IntegritySeals.NoteContentHashAsync(db, provider, id, Ct);
    }

    private static async Task<string?> DocumentHashAsync(RetentionScratch s, Guid provider, Guid id)
    {
        await using var db = s.CreateDbContext();
        return await IntegritySeals.DocumentContentHashAsync(db, provider, id, Ct);
    }

    private static async Task<List<IntegritySeal>> SealsAsync(RetentionScratch s, Guid provider)
    {
        await using var db = s.CreateDbContext();
        return await db.IntegritySeals.AsNoTracking().Where(x => x.ProviderId == provider).OrderBy(x => x.Sequence).ToListAsync(Ct);
    }

    [Fact]
    public void SealHash_FollowsTheSpecFormat()
    {
        // Arrange
        var subject = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");
        var sealedAt = new DateTimeOffset(2026, 11, 3, 11, 0, 0, 123, TimeSpan.FromHours(-3)).AddTicks(4560);

        // Act
        var first = IntegritySeals.SealHash(null, 1, "note", subject, "cafe", sealedAt);
        var second = IntegritySeals.SealHash("prev", 2, "document", subject, "cafe", sealedAt);

        // Assert
        Hex("|1|note|aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee|cafe|2026-11-03T14:00:00.123456Z").Should().Be(first);
        Hex("prev|2|document|aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee|cafe|2026-11-03T14:00:00.123456Z").Should().Be(second);
    }

    private static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task SignNote_CreatesTheSealInTheSameTransaction()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var noteId = await AddReadyNoteAsync(s, provider, patient);
        await using var db = s.CreateDbContext();

        // Act
        var outcome = await NoteEditing.SignAsync(db, provider, noteId, Now, Ct);

        // Assert
        outcome.Should().Be(SignOutcome.Signed);
        var seal = (await SealsAsync(s, provider)).Should().ContainSingle().Subject;
        seal.Sequence.Should().Be(1);
        seal.SubjectType.Should().Be("note");
        seal.SubjectId.Should().Be(noteId);
        seal.PreviousHash.Should().BeNull();
        seal.SealedAt.Should().Be(Now);
        seal.Retroactive.Should().BeFalse();
        seal.DiscardedAt.Should().BeNull();
        seal.ContentHash.Should().Be(await NoteHashAsync(s, provider, noteId));
        seal.Hash.Should().Be(IntegritySeals.SealHash(null, 1, "note", noteId, seal.ContentHash, seal.SealedAt));
    }

    [Fact]
    public async Task SignDocument_CreatesTheSealAndTheNextOneChainsToIt()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var noteId = await AddReadyNoteAsync(s, provider, patient);
        var documentId = await AddReadyDocumentAsync(s, provider, patient);
        await using var db = s.CreateDbContext();

        // Act
        await NoteEditing.SignAsync(db, provider, noteId, Now, Ct);
        var result = await ClinicalDocumentService.SignAsync(db, provider, documentId, false, Now.AddMinutes(1), Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        var seals = await SealsAsync(s, provider);
        seals.Select(x => x.Sequence).Should().Equal(1, 2);
        var seal = seals[1];
        seal.SubjectType.Should().Be("document");
        seal.SubjectId.Should().Be(documentId);
        seal.PreviousHash.Should().Be(seals[0].Hash);
        seal.ContentHash.Should().Be(await DocumentHashAsync(s, provider, documentId));
        seal.Hash.Should().Be(IntegritySeals.SealHash(seals[0].Hash, 2, "document", documentId, seal.ContentHash, seal.SealedAt));
    }

    [Fact]
    public async Task SignDocument_RefusedOutcomes_LeaveNoSeal()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await using var db = s.CreateDbContext();
        var created = await ClinicalDocumentService.CreateAsync(db, provider, patient, "atestado", null, Now, Ct);

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, provider, created.Id!.Value, false, Now, Ct);

        // Assert
        result.Outcome.Should().NotBe(DocumentOutcome.Ok);
        (await SealsAsync(s, provider)).Should().BeEmpty();
    }

    [Fact]
    public async Task CorrectedDocument_GetsItsOwnSeal_TheOldOneIsUntouched()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var original = await AddReadyDocumentAsync(s, provider, patient);
        await using var db = s.CreateDbContext();
        await ClinicalDocumentService.SignAsync(db, provider, original, false, Now, Ct);
        var before = (await SealsAsync(s, provider)).Single();

        // Act
        var copy = (await ClinicalDocumentService.CorrectAsync(db, provider, original, Now.AddHours(1), Ct)).Id!.Value;
        (await SealsAsync(s, provider)).Should().ContainSingle("a draft correction is not sealed yet");
        await ClinicalDocumentService.SaveAsync(db, provider, copy, new Dictionary<string, string>(CompleteAtestado) { ["finalidade"] = "Outra" }, AtestadoConfirmations, [], Now.AddHours(1), Ct);
        var signed = await ClinicalDocumentService.SignAsync(db, provider, copy, false, Now.AddHours(2), Ct);

        // Assert
        signed.Outcome.Should().Be(DocumentOutcome.Ok);
        var seals = await SealsAsync(s, provider);
        seals.Should().HaveCount(2);
        seals[0].Should().BeEquivalentTo(before);
        seals[1].SubjectId.Should().Be(copy);
        seals[1].PreviousHash.Should().Be(before.Hash);
        seals[1].ContentHash.Should().NotBe(before.ContentHash);
    }

    [Fact]
    public async Task ConcurrentSignings_ByTheSameProvider_GetConsecutiveSequencesAndALinkedChain()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var notes = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            notes.Add(await AddReadyNoteAsync(s, provider, patient));
        }

        // Act
        var outcomes = await Task.WhenAll(notes.Select(async id =>
        {
            await using var db = s.CreateDbContext();
            return await NoteEditing.SignAsync(db, provider, id, Now, Ct);
        }));

        // Assert
        outcomes.Should().AllBeEquivalentTo(SignOutcome.Signed);
        var seals = await SealsAsync(s, provider);
        seals.Select(x => x.Sequence).Should().Equal(1, 2, 3, 4, 5, 6);
        seals.Select(x => x.SubjectId).Should().BeEquivalentTo(notes);
        seals[0].PreviousHash.Should().BeNull();
        for (var i = 1; i < seals.Count; i++)
        {
            seals[i].PreviousHash.Should().Be(seals[i - 1].Hash);
        }
    }

    [Fact]
    public async Task DifferentProviders_HaveTheirOwnChains()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var first = await s.AddProviderAsync(Ct, withCrp: true);
        var second = await s.AddProviderAsync(Ct, withCrp: true);
        var firstNote = await AddReadyNoteAsync(s, first, await s.AddPatientAsync(first, "Ana", Ct));
        var secondNote = await AddReadyNoteAsync(s, second, await s.AddPatientAsync(second, "Bia", Ct));

        // Act
        await using (var db = s.CreateDbContext())
        {
            await NoteEditing.SignAsync(db, first, firstNote, Now, Ct);
        }
        await using (var db = s.CreateDbContext())
        {
            await NoteEditing.SignAsync(db, second, secondNote, Now, Ct);
        }

        // Assert
        (await SealsAsync(s, first)).Single().Sequence.Should().Be(1);
        (await SealsAsync(s, second)).Single().Sequence.Should().Be(1);
    }

    [Fact]
    public async Task SignNote_WhenSealingFails_RollsTheSigningBack()
    {
        // Arrange: a seal already exists for the note, so the new one violates the subject key.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var noteId = await AddReadyNoteAsync(s, provider, patient);
        await s.ExecuteAsync(
            """
            INSERT INTO "IntegritySeals" ("ProviderId","Sequence","SubjectType","SubjectId","ContentHash","Hash","SealedAt")
            VALUES (@p, 1, 'note', @n, repeat('a', 64), repeat('b', 64), now())
            """, Ct, ("p", provider), ("n", noteId));
        await using var db = s.CreateDbContext();

        // Act
        var sign = async () => await NoteEditing.SignAsync(db, provider, noteId, Now, Ct);

        // Assert
        await sign.Should().ThrowAsync<Exception>();
        (await s.CountAsync("""SELECT count(*) FROM "Notes" WHERE "Id" = @n AND "Status" = 'ReadyForReview' AND "SignedAt" IS NULL""", Ct, ("n", noteId))).Should().Be(1);
        (await s.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "NoteId" = @n AND "Action" = 'signed'""", Ct, ("n", noteId))).Should().Be(0);
    }

    [Fact]
    public async Task SignDocument_WhenSealingFails_RollsTheSigningBack()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var documentId = await AddReadyDocumentAsync(s, provider, patient);
        await s.ExecuteAsync(
            """
            INSERT INTO "IntegritySeals" ("ProviderId","Sequence","SubjectType","SubjectId","ContentHash","Hash","SealedAt")
            VALUES (@p, 1, 'document', @d, repeat('a', 64), repeat('b', 64), now())
            """, Ct, ("p", provider), ("d", documentId));
        await using var db = s.CreateDbContext();

        // Act
        var sign = async () => await ClinicalDocumentService.SignAsync(db, provider, documentId, false, Now, Ct);

        // Assert
        await sign.Should().ThrowAsync<Exception>();
        (await s.CountAsync("""SELECT count(*) FROM "ClinicalDocuments" WHERE "Id" = @d AND "SignedAt" IS NULL""", Ct, ("d", documentId))).Should().Be(1);
        (await s.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "DocumentId" = @d AND "Action" = 'document_signed'""", Ct, ("d", documentId))).Should().Be(0);
    }

    [Fact]
    public async Task SealAsync_WithoutATransaction_Throws()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        await using var db = s.CreateDbContext();

        // Act
        var seal = async () => await IntegritySeals.SealAsync(db, provider, "note", Guid.NewGuid(), new string('a', 64), Now, Ct);

        // Assert
        await seal.Should().ThrowAsync<InvalidOperationException>();
        (await SealsAsync(s, provider)).Should().BeEmpty();
    }

    [Fact]
    public async Task ContentHashes_AreNullForUnsignedOrForeignRecords()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var stranger = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var draftNote = await AddReadyNoteAsync(s, provider, patient);
        var draftDocument = await AddReadyDocumentAsync(s, provider, patient);
        var signedDocument = await s.AddSignedDocumentAsync(provider, patient, Now, Ct);
        await using var db = s.CreateDbContext();

        // Act / Assert
        (await IntegritySeals.NoteContentHashAsync(db, provider, draftNote, Ct)).Should().BeNull();
        (await IntegritySeals.DocumentContentHashAsync(db, provider, draftDocument, Ct)).Should().BeNull();
        (await IntegritySeals.DocumentContentHashAsync(db, stranger, signedDocument, Ct)).Should().BeNull();
        (await IntegritySeals.DocumentContentHashAsync(db, provider, signedDocument, Ct)).Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task Discard_MarksTheSealsOfThePatient_KeepsThemAndLeavesOtherPatientsAlone()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var record = await DiscardServiceTests.SeedExpiredAsync(s, store);
        var other = await DiscardServiceTests.SeedExpiredAsync(s, store, record.Provider, "Bia");
        var subjects = new[]
        {
            ("note", record.Notes[0]), ("document", record.Documents[0]), ("note", other.Notes[0]),
        };
        string? previous = null;
        for (var i = 0; i < subjects.Length; i++)
        {
            var hash = new string((char)('a' + i), 64);
            await s.ExecuteAsync(
                """
                INSERT INTO "IntegritySeals" ("ProviderId","Sequence","SubjectType","SubjectId","ContentHash","PreviousHash","Hash","SealedAt")
                VALUES (@p, @seq, @type, @subject, repeat('c', 64), @prev, @hash, now())
                """, Ct, ("p", record.Provider), ("seq", (long)i + 1), ("type", subjects[i].Item1), ("subject", subjects[i].Item2),
                ("prev", (object?)previous ?? DBNull.Value), ("hash", hash));
            previous = hash;
        }
        var discardAt = new DateTimeOffset(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);

        // Act
        var result = await DiscardService.DiscardAsync(
            s.CreateDbContext(), store, NullLogger.Instance, record.Provider, record.Patient, "Ana Silva", discardAt, Ct);

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.Ok);
        var seals = await SealsAsync(s, record.Provider);
        seals.Should().HaveCount(3);
        seals[0].DiscardedAt.Should().Be(discardAt);
        seals[1].DiscardedAt.Should().Be(discardAt);
        seals[2].DiscardedAt.Should().BeNull();
        seals.Select(x => x.Hash).Should().Equal(new string('a', 64), new string('b', 64), new string('c', 64));
        (await s.CountAsync("""SELECT count(*) FROM "Notes" WHERE "PatientId" = @pa""", Ct, ("pa", record.Patient))).Should().Be(0);
    }
}
