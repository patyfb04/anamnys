using System.Security.Cryptography;
using System.Text;
using Anamnys.Server.Notes;
using Anamnys.Server.Storage;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;

namespace Anamnys.Tests.Notes;

[Collection(SharedAppHostCollection.Name)]
public class NoteExportTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static NoteExportTests() => QuestPDF.Settings.License = LicenseType.Community;

    [Fact]
    public async Task ExportAsync_FirstExport_UploadsStoresHashMarksExportedAndAudits()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddSignedNoteAsync(seed);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteExport.ExportAsync(db, store, seed.ProviderId, noteId, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportOutcome.Ok);
        store.Puts.Should().Be(1);
        var key = $"notes/{seed.ProviderId}/{noteId}.pdf";
        store.Objects.Should().ContainKey(key);
        Encoding.ASCII.GetString(store.Objects[key], 0, 4).Should().Be("%PDF");
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.Status.Should().Be("Exported");
        note.PdfObjectKey.Should().Be(key);
        note.PdfSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(store.Objects[key])));
        note.ExportedAt.Should().Be(Now);
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.NoteId == noteId).ToListAsync(Ct);
        var entry = audit.Should().ContainSingle().Subject;
        entry.Action.Should().Be("exported");
        entry.NewValue.Should().Be(note.PdfSha256);
        entry.ActorId.Should().Be(seed.ProviderId);
        result.Url!.ToString().Should().Contain("nota-").And.Contain("ttl=3600");
        result.ExpiresAt.Should().Be(Now + TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ExportAsync_SecondExport_DoesNotUploadAndAuditsLink()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddSignedNoteAsync(seed);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        await NoteExport.ExportAsync(db, store, seed.ProviderId, noteId, Now, Ct);

        // Act
        var second = await NoteExport.ExportAsync(db, store, seed.ProviderId, noteId, Now.AddMinutes(5), Ct);

        // Assert
        second.Outcome.Should().Be(ExportOutcome.Ok);
        store.Puts.Should().Be(1);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.ExportedAt.Should().Be(Now);
        var actions = await db.AuditEntries.AsNoTracking().Where(a => a.NoteId == noteId)
            .OrderBy(a => a.At).Select(a => a.Action).ToListAsync(Ct);
        actions.Should().Equal("exported", "pdf_link_issued");
        second.ExpiresAt.Should().Be(Now.AddMinutes(5) + TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ExportAsync_UnsignedNote_NotSigned()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddSignedNoteAsync(seed, signed: false);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteExport.ExportAsync(db, store, seed.ProviderId, noteId, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportOutcome.NotSigned);
        store.Puts.Should().Be(0);
    }

    [Fact]
    public async Task ExportAsync_OtherProvider_NotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddSignedNoteAsync(seed);
        var other = await seed.AddProviderAsync(Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteExport.ExportAsync(db, store, other, noteId, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportOutcome.NotFound);
        store.Puts.Should().Be(0);
    }

    [Fact]
    public async Task ExportAsync_StoreFails_StorageUnavailableAndNoteUnchanged()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddSignedNoteAsync(seed);
        var store = new FakeNoteFileStore { Fail = true };
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteExport.ExportAsync(db, store, seed.ProviderId, noteId, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportOutcome.StorageUnavailable);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.Status.Should().Be("Signed");
        note.PdfObjectKey.Should().BeNull();
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.NoteId == noteId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ExportAsync_StoreTimesOut_StorageUnavailable()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddSignedNoteAsync(seed);
        var store = new FakeNoteFileStore { FailWith = new TaskCanceledException("timeout") };
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteExport.ExportAsync(db, store, seed.ProviderId, noteId, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportOutcome.StorageUnavailable);
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        note.Status.Should().Be("Signed");
        note.PdfObjectKey.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ExportedNote_ReturnsExportedAt()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddSignedNoteAsync(seed);
        await using var db = seed.CreateDbContext();
        await NoteExport.ExportAsync(db, new FakeNoteFileStore(), seed.ProviderId, noteId, Now, Ct);

        // Act
        var detail = await NoteQueries.GetAsync(db, seed.ProviderId, noteId, Ct);

        // Assert
        detail!.ExportedAt.Should().Be(Now);
    }

    [Fact]
    public async Task ExportAsync_RealStore_PresignedUrlDownloadsTheStoredPdf()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var noteId = await AddSignedNoteAsync(seed);
        using var store = new S3NoteFileStore(fixture.StorageOptions);
        await store.EnsureBucketAsync(Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await NoteExport.ExportAsync(db, store, seed.ProviderId, noteId, DateTimeOffset.UtcNow, Ct);
        using var http = new HttpClient();
        using var response = await http.GetAsync(result.Url, Ct);
        var body = await response.Content.ReadAsByteArrayAsync(Ct);

        // Assert
        result.Outcome.Should().Be(ExportOutcome.Ok);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId, Ct);
        Convert.ToHexStringLower(SHA256.HashData(body)).Should().Be(note.PdfSha256);
    }

    private async Task<Guid> AddSignedNoteAsync(PatientSearchSeed seed, bool signed = true)
    {
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var id = Guid.NewGuid();
        await seed.ExecuteSqlAsync(
            signed
                ? """
                  INSERT INTO "Notes" ("Id", "ProviderId", "PatientId", "Status", "InputMode", "Format", "StructureStatus", "RawTranscript", "SignedAt", "SignedByCrp")
                  VALUES (@id, @provider, @patient, 'Signed', 'VoiceLive', 'DAP', 'done', 'transcricao da sessao', @signedAt, '06/123456')
                  """
                : """
                  INSERT INTO "Notes" ("Id", "ProviderId", "PatientId", "Status", "InputMode", "Format", "StructureStatus", "RawTranscript")
                  VALUES (@id, @provider, @patient, 'ReadyForReview', 'VoiceLive', 'DAP', 'done', 'transcricao da sessao')
                  """,
            Ct, ("id", id), ("provider", seed.ProviderId), ("patient", patient), ("signedAt", Now.AddHours(-1)));
        await seed.ExecuteSqlAsync(
            """
            INSERT INTO "NoteSections" ("Id", "NoteId", "Key", "Content", "Position", "GeneratedAt", "Revision") VALUES
            (gen_random_uuid(), @id, 'data', 'Relata insônia.', 0, now(), 0),
            (gen_random_uuid(), @id, 'assessment', '', 1, now(), 0),
            (gen_random_uuid(), @id, 'plan', 'Retorno.', 2, now(), 0)
            """,
            Ct, ("id", id));
        return id;
    }
}
