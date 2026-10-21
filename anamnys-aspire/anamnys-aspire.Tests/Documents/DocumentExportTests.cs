using System.Security.Cryptography;
using System.Text;
using Anamnys.Server.Documents;
using Anamnys.Server.Notes;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;

namespace Anamnys.Tests.Documents;

[Collection(SharedAppHostCollection.Name)]
public class DocumentExportTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DocumentExportTests() => QuestPDF.Settings.License = LicenseType.Community;

    private async Task<(PatientSearchSeed Seed, Guid DocumentId)> SeedDocumentAsync(bool signed = true)
    {
        var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await seed.ExecuteSqlAsync(
            """
            UPDATE "Providers" SET "CrpNumber" = '12345', "CrpRegion" = '06', "PracticeCity" = 'São Paulo' WHERE "Id" = @id
            """,
            Ct, ("id", seed.ProviderId));
        var patientId = await seed.AddPatientAsync("Ana", "Souza", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var created = await ClinicalDocumentService.CreateAsync(db, seed.ProviderId, patientId, "declaracao", null, Now, Ct);
        var id = created.Id!.Value;
        await ClinicalDocumentService.SaveAsync(
            db, seed.ProviderId, id,
            new Dictionary<string, string>
            {
                ["finalidade"] = "Comprovar acompanhamento.",
                ["local_dias_horarios_e_duracao_do_acompanhamento"] = "Consultório, às terças.",
                ["prazo_validade"] = "30 dias.",
            },
            ["sintomas_situacoes_ou_estados_psicologicos"], [], Now, Ct);
        if (signed)
        {
            var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);
            result.Outcome.Should().Be(DocumentOutcome.Ok);
        }
        return (seed, id);
    }

    [Fact]
    public async Task ExportAsync_FirstExport_UploadsStoresHashAndAudits()
    {
        // Arrange
        var (seed, id) = await SeedDocumentAsync();
        await using var _ = seed;
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();

        // Act
        var result = await DocumentExport.ExportAsync(db, store, seed.ProviderId, id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportOutcome.Ok);
        store.Puts.Should().Be(1);
        var key = $"documents/{seed.ProviderId}/{id}.pdf";
        store.Objects.Should().ContainKey(key);
        Encoding.ASCII.GetString(store.Objects[key], 0, 4).Should().Be("%PDF");
        var document = await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        document.PdfObjectKey.Should().Be(key);
        document.PdfSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(store.Objects[key])));
        document.ExportedAt.Should().Be(Now);
        var audit = await db.AuditEntries.AsNoTracking()
            .Where(a => a.DocumentId == id && a.Action == "document_exported").ToListAsync(Ct);
        var entry = audit.Should().ContainSingle().Subject;
        entry.NewValue.Should().Be(document.PdfSha256);
        entry.ActorId.Should().Be(seed.ProviderId);
        result.Url!.ToString().Should().Contain("documento-").And.Contain("ttl=3600");
        result.ExpiresAt.Should().Be(Now + TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ExportAsync_SecondExport_DoesNotUploadAndAuditsLink()
    {
        // Arrange
        var (seed, id) = await SeedDocumentAsync();
        await using var _ = seed;
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        await DocumentExport.ExportAsync(db, store, seed.ProviderId, id, Now, Ct);

        // Act
        var second = await DocumentExport.ExportAsync(db, store, seed.ProviderId, id, Now.AddMinutes(5), Ct);

        // Assert
        second.Outcome.Should().Be(ExportOutcome.Ok);
        store.Puts.Should().Be(1);
        var document = await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        document.ExportedAt.Should().Be(Now);
        var actions = await db.AuditEntries.AsNoTracking().Where(a => a.DocumentId == id && (a.Action == "document_exported" || a.Action == "document_link_issued"))
            .OrderBy(a => a.At).Select(a => a.Action).ToListAsync(Ct);
        actions.Should().Equal("document_exported", "document_link_issued");
        second.ExpiresAt.Should().Be(Now.AddMinutes(5) + TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task ExportAsync_DraftDocument_NotSigned()
    {
        // Arrange
        var (seed, id) = await SeedDocumentAsync(signed: false);
        await using var _ = seed;
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();

        // Act
        var result = await DocumentExport.ExportAsync(db, store, seed.ProviderId, id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportOutcome.NotSigned);
        store.Puts.Should().Be(0);
    }

    [Fact]
    public async Task ExportAsync_SupersededDocument_StillExports()
    {
        // Arrange
        var (seed, id) = await SeedDocumentAsync();
        await using var _ = seed;
        await using var db = seed.CreateDbContext();
        (await ClinicalDocumentService.CorrectAsync(db, seed.ProviderId, id, Now, Ct)).Outcome.Should().Be(DocumentOutcome.Ok);
        var store = new FakeNoteFileStore();

        // Act
        var result = await DocumentExport.ExportAsync(db, store, seed.ProviderId, id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportOutcome.Ok);
        store.Puts.Should().Be(1);
    }

    [Fact]
    public async Task ExportAsync_OtherProvider_NotFound()
    {
        // Arrange
        var (seed, id) = await SeedDocumentAsync();
        await using var _ = seed;
        var other = await seed.AddProviderAsync(Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();

        // Act
        var result = await DocumentExport.ExportAsync(db, store, other, id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportOutcome.NotFound);
        store.Puts.Should().Be(0);
    }

    [Fact]
    public async Task ExportAsync_StoreFails_StorageUnavailableAndNothingWritten()
    {
        // Arrange
        var (seed, id) = await SeedDocumentAsync();
        await using var _ = seed;
        var store = new FakeNoteFileStore { Fail = true };
        await using var db = seed.CreateDbContext();

        // Act
        var result = await DocumentExport.ExportAsync(db, store, seed.ProviderId, id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportOutcome.StorageUnavailable);
        var document = await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        document.PdfObjectKey.Should().BeNull();
        document.ExportedAt.Should().BeNull();
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.DocumentId == id && a.Action == "document_exported", Ct)).Should().Be(0);
    }
}
