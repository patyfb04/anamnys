using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.ExternalDocuments;

[Collection(SharedAppHostCollection.Name)]
public class ExternalDocumentServiceTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Pdf = [.. "%PDF-1.7\nbody"u8];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ExternalDocumentUpload PdfUpload(string name = "Laudo da Maria.pdf", string kind = "laudo") =>
        new(name, Pdf, kind, new DateOnly(2026, 9, 1), "Neuropsicológico");

    internal static async Task<ExternalDocument> AddDocumentAsync(
        AnamnysDbContext db, FakeNoteFileStore store, Guid providerId, Guid patientId, string status = "clean",
        string kind = "laudo", DateTimeOffset? uploadedAt = null, bool withObject = true, DateTimeOffset? deletedAt = null)
    {
        var document = new ExternalDocument
        {
            Id = Guid.NewGuid(),
            PatientId = patientId,
            ProviderId = providerId,
            Filename = "original.pdf",
            MimeType = "application/pdf",
            ByteSize = Pdf.Length,
            ScanStatus = status,
            UploadedAt = uploadedAt ?? Now,
            Kind = kind,
            DeletedAt = deletedAt,
        };
        if (withObject)
        {
            document.ObjectKey = ExternalDocumentService.ObjectKeyFor(providerId, document.Id);
            store.Objects[document.ObjectKey] = Pdf;
        }
        db.ExternalDocuments.Add(document);
        await db.SaveChangesAsync(Ct);
        return document;
    }

    [Fact]
    public async Task UploadAsync_ValidPdf_StoresUnderAnOpaqueKey_AddsAPendingRow_QueuesTheScan_AndAudits()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var store = new FakeNoteFileStore();
        var queue = new ScanQueue();

        // Act
        var result = await ExternalDocumentService.UploadAsync(db, store, queue, seed.ProviderId, patient, PdfUpload(), Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExternalDocumentOutcome.Ok);
        var row = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == result.Id, Ct);
        row.ScanStatus.Should().Be("pending");
        row.ProviderId.Should().Be(seed.ProviderId);
        row.PatientId.Should().Be(patient);
        row.Filename.Should().Be("Laudo da Maria.pdf");
        row.MimeType.Should().Be("application/pdf");
        row.ByteSize.Should().Be(Pdf.Length);
        row.Kind.Should().Be("laudo");
        row.DocumentDate.Should().Be(new DateOnly(2026, 9, 1));
        row.Description.Should().Be("Neuropsicológico");
        row.UploadedAt.Should().Be(Now);
        row.ScanAttempts.Should().Be(0);
        row.ObjectKey.Should().Be($"external/{seed.ProviderId}/{row.Id}");
        store.Objects.Should().ContainKey(row.ObjectKey!);
        store.Objects.Keys.Should().OnlyContain(k => !k.Contains("Laudo") && !k.Contains("Maria") && !k.Contains(".pdf"));
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job.Should().Be(new ScanJob(row.Id, seed.ProviderId));
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.ActorId == seed.ProviderId, Ct);
        audit.Action.Should().Be("external_document_uploaded");
        audit.NoteId.Should().BeNull();
        audit.DocumentId.Should().BeNull();
        audit.NewValue.Should().Be($"{row.Id} laudo");
        $"{audit.FieldChanged}{audit.PreviousValue}{audit.NewValue}".Should().NotContain("Laudo").And.NotContain("Maria");
    }

    [Fact]
    public async Task UploadAsync_FileNameWithPathAndControlCharacters_IsStoredAsPlainDisplayName()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ExternalDocumentService.UploadAsync(
            db, new FakeNoteFileStore(), new ScanQueue(), seed.ProviderId, patient,
            PdfUpload("C:\\fakepath\\..\\exame\u0007.pdf"), Now, Ct);

        // Assert
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == result.Id, Ct)).Filename.Should().Be("exame.pdf");
    }

    [Theory]
    [InlineData("empty", ExternalDocumentOutcome.Empty)]
    [InlineData("text", ExternalDocumentOutcome.UnsupportedType)]
    [InlineData("mismatch", ExternalDocumentOutcome.TypeMismatch)]
    [InlineData("big", ExternalDocumentOutcome.TooLarge)]
    public async Task UploadAsync_InvalidFile_IsRefused_AndLeavesNoTrace(string scenario, ExternalDocumentOutcome expected)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var store = new FakeNoteFileStore();
        var queue = new ScanQueue();
        var big = new byte[ExternalDocumentService.MaxBytes + 1];
        "%PDF-"u8.CopyTo(big);
        var upload = scenario switch
        {
            "empty" => PdfUpload() with { Content = [] },
            "text" => PdfUpload("a.txt") with { Content = "hello"u8.ToArray() },
            "mismatch" => PdfUpload("a.png"),
            _ => PdfUpload() with { Content = big },
        };

        // Act
        var result = await ExternalDocumentService.UploadAsync(db, store, queue, seed.ProviderId, patient, upload, Now, Ct);

        // Assert
        result.Outcome.Should().Be(expected);
        store.Objects.Should().BeEmpty();
        queue.Reader.TryRead(out _).Should().BeFalse();
        (await db.ExternalDocuments.CountAsync(d => d.ProviderId == seed.ProviderId, Ct)).Should().Be(0);
        (await db.AuditEntries.CountAsync(a => a.ActorId == seed.ProviderId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task UploadAsync_FileOfExactlyTwentyMegabytes_IsAccepted()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var content = new byte[ExternalDocumentService.MaxBytes];
        "%PDF-"u8.CopyTo(content);

        // Act
        var result = await ExternalDocumentService.UploadAsync(
            db, new FakeNoteFileStore(), new ScanQueue(), seed.ProviderId, patient, PdfUpload() with { Content = content }, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExternalDocumentOutcome.Ok);
    }

    [Fact]
    public async Task UploadAsync_AnotherProvidersPatient_IsNotFound_AndAnArchivedPatientIsRefused()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var foreign = await seed.AddPatientAsync("Joana", "Souza", providerId: other, cancellationToken: Ct);
        var archived = await seed.AddPatientAsync("Ana", "Lima", archivedAt: Now, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var store = new FakeNoteFileStore();

        // Act
        var notFound = await ExternalDocumentService.UploadAsync(db, store, new ScanQueue(), seed.ProviderId, foreign, PdfUpload(), Now, Ct);
        var missing = await ExternalDocumentService.UploadAsync(db, store, new ScanQueue(), seed.ProviderId, Guid.NewGuid(), PdfUpload(), Now, Ct);
        var refused = await ExternalDocumentService.UploadAsync(db, store, new ScanQueue(), seed.ProviderId, archived, PdfUpload(), Now, Ct);

        // Assert
        notFound.Outcome.Should().Be(ExternalDocumentOutcome.NotFound);
        missing.Outcome.Should().Be(ExternalDocumentOutcome.NotFound);
        refused.Outcome.Should().Be(ExternalDocumentOutcome.Archived);
        store.Objects.Should().BeEmpty();
        (await db.ExternalDocuments.CountAsync(d => d.PatientId == foreign || d.PatientId == archived, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task UploadAsync_StoreDown_Throws_AndLeavesNoRow()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var queue = new ScanQueue();

        // Act
        var act = () => ExternalDocumentService.UploadAsync(
            db, new FakeNoteFileStore { Fail = true }, queue, seed.ProviderId, patient, PdfUpload(), Now, Ct);

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
        (await db.ExternalDocuments.CountAsync(d => d.ProviderId == seed.ProviderId, Ct)).Should().Be(0);
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("clean", true)]
    [InlineData("pending", false)]
    [InlineData("infected", false)]
    [InlineData("failed", false)]
    public async Task OpenAsync_OnlyACleanDocument_GetsAnInlineLink_AndTheOpeningIsAudited(string status, bool opens)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var store = new FakeNoteFileStore();
        var document = await AddDocumentAsync(db, store, seed.ProviderId, patient, status, withObject: status != "infected");

        // Act
        var result = await ExternalDocumentService.OpenAsync(db, store, seed.ProviderId, document.Id, Now, Ct);

        // Assert
        var audits = await db.AuditEntries.AsNoTracking().Where(a => a.ActorId == seed.ProviderId).ToListAsync(Ct);
        if (opens)
        {
            result.Outcome.Should().Be(ExternalDocumentOutcome.Ok);
            result.Url!.ToString().Should().Contain(document.ObjectKey!).And.Contain("inline=1").And.Contain("type=application/pdf").And.Contain("ttl=3600");
            result.Url.ToString().Should().NotContain("original");
            result.ExpiresAt.Should().Be(Now.AddHours(1));
            audits.Should().ContainSingle().Which.Action.Should().Be("external_document_opened");
        }
        else
        {
            result.Outcome.Should().Be(ExternalDocumentOutcome.NotAvailable);
            result.Url.Should().BeNull();
            audits.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task OpenAsync_AnotherProvidersDocument_OrADeletedOne_IsNotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var store = new FakeNoteFileStore();
        var mine = await AddDocumentAsync(db, store, seed.ProviderId, patient);
        var deleted = await AddDocumentAsync(db, store, seed.ProviderId, patient, withObject: false, deletedAt: Now);

        // Act
        var foreign = await ExternalDocumentService.OpenAsync(db, store, other, mine.Id, Now, Ct);
        var gone = await ExternalDocumentService.OpenAsync(db, store, seed.ProviderId, deleted.Id, Now, Ct);
        var unknown = await ExternalDocumentService.OpenAsync(db, store, seed.ProviderId, Guid.NewGuid(), Now, Ct);

        // Assert
        foreign.Outcome.Should().Be(ExternalDocumentOutcome.NotFound);
        gone.Outcome.Should().Be(ExternalDocumentOutcome.NotFound);
        unknown.Outcome.Should().Be(ExternalDocumentOutcome.NotFound);
        (await db.AuditEntries.CountAsync(a => a.Action == "external_document_opened" && a.ActorId == other, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task OpenAsync_ArchivedPatientsDocument_StillOpens()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Lima", archivedAt: Now, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var store = new FakeNoteFileStore();
        var document = await AddDocumentAsync(db, store, seed.ProviderId, patient);

        // Act
        var result = await ExternalDocumentService.OpenAsync(db, store, seed.ProviderId, document.Id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExternalDocumentOutcome.Ok);
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletesTheRow_RemovesTheObject_AndAudits()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var store = new FakeNoteFileStore();
        var document = await AddDocumentAsync(db, store, seed.ProviderId, patient);
        var key = document.ObjectKey!;

        // Act
        var result = await ExternalDocumentService.DeleteAsync(db, store, seed.ProviderId, document.Id, Now, Ct);
        var again = await ExternalDocumentService.DeleteAsync(db, store, seed.ProviderId, document.Id, Now, Ct);

        // Assert
        result.Should().Be(ExternalDocumentOutcome.Ok);
        again.Should().Be(ExternalDocumentOutcome.NotFound);
        var row = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        row.DeletedAt.Should().Be(Now);
        row.ObjectKey.Should().BeNull();
        store.Objects.Should().NotContainKey(key);
        store.Deleted.Should().Contain(key);
        (await db.AuditEntries.AsNoTracking().SingleAsync(a => a.ActorId == seed.ProviderId, Ct)).Action.Should().Be("external_document_deleted");
    }

    [Fact]
    public async Task DeleteAsync_ArchivedPatient_AnotherProvider_AndAFailingStore_ChangeNothing()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var archivedPatient = await seed.AddPatientAsync("Ana", "Lima", archivedAt: Now, cancellationToken: Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var store = new FakeNoteFileStore();
        var inArchived = await AddDocumentAsync(db, store, seed.ProviderId, archivedPatient);
        var mine = await AddDocumentAsync(db, store, seed.ProviderId, patient);

        // Act
        var archived = await ExternalDocumentService.DeleteAsync(db, store, seed.ProviderId, inArchived.Id, Now, Ct);
        var foreign = await ExternalDocumentService.DeleteAsync(db, store, other, mine.Id, Now, Ct);
        store.FailDelete = true;
        var failing = () => ExternalDocumentService.DeleteAsync(db, store, seed.ProviderId, mine.Id, Now, Ct);

        // Assert
        archived.Should().Be(ExternalDocumentOutcome.Archived);
        foreign.Should().Be(ExternalDocumentOutcome.NotFound);
        await failing.Should().ThrowAsync<HttpRequestException>();
        db.ChangeTracker.Clear();
        (await db.ExternalDocuments.AsNoTracking().Where(d => d.ProviderId == seed.ProviderId).ToListAsync(Ct))
            .Should().OnlyContain(d => d.DeletedAt == null && d.ObjectKey != null);
        store.Objects.Should().HaveCount(2);
        (await db.AuditEntries.CountAsync(a => a.ActorId == seed.ProviderId || a.ActorId == other, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ListAsync_ReturnsThePatientsLiveDocuments_NewestFirst()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var another = await seed.AddPatientAsync("Joana", "Souza", cancellationToken: Ct);
        var foreign = await seed.AddPatientAsync("Rita", "Costa", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var store = new FakeNoteFileStore();
        var older = await AddDocumentAsync(db, store, seed.ProviderId, patient, uploadedAt: Now.AddDays(-2));
        var newer = await AddDocumentAsync(db, store, seed.ProviderId, patient, "pending", "receita", Now);
        await AddDocumentAsync(db, store, seed.ProviderId, patient, uploadedAt: Now.AddDays(-1), withObject: false, deletedAt: Now);
        await AddDocumentAsync(db, store, seed.ProviderId, another);

        // Act
        var items = await ExternalDocumentService.ListAsync(db, seed.ProviderId, patient, Ct);
        var foreignItems = await ExternalDocumentService.ListAsync(db, seed.ProviderId, foreign, Ct);

        // Assert
        items!.Select(i => i.Id).Should().Equal(newer.Id, older.Id);
        items[0].Should().Match<ExternalDocumentItem>(i =>
            i.Kind == "receita" && i.ScanStatus == "pending" && i.FileName == "original.pdf" && i.PatientName == null && i.PatientId == patient);
        foreignItems.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_ScopesToTheProvider_FiltersByKindPatientAndName_AndPages()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var maria = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var joana = await seed.AddPatientAsync("Joana", "Souza", cancellationToken: Ct);
        var foreign = await seed.AddPatientAsync("Maria", "Silva", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var store = new FakeNoteFileStore();
        for (var i = 0; i < 30; i++)
        {
            await AddDocumentAsync(db, store, seed.ProviderId, maria, kind: "exame", uploadedAt: Now.AddMinutes(-i));
        }
        var receita = await AddDocumentAsync(db, store, seed.ProviderId, joana, kind: "receita", uploadedAt: Now.AddDays(-1));
        await AddDocumentAsync(db, store, seed.ProviderId, joana, kind: "receita", withObject: false, deletedAt: Now);
        await AddDocumentAsync(db, store, other, foreign, kind: "receita");

        // Act
        var everything = await ExternalDocumentService.SearchAsync(db, seed.ProviderId, new(null, null, null, null), Ct);
        var secondPage = await ExternalDocumentService.SearchAsync(db, seed.ProviderId, new(null, null, null, 2), Ct);
        var byKind = await ExternalDocumentService.SearchAsync(db, seed.ProviderId, new("receita", null, null, null), Ct);
        var byName = await ExternalDocumentService.SearchAsync(db, seed.ProviderId, new(null, null, "joana so", null), Ct);
        var byPatient = await ExternalDocumentService.SearchAsync(db, seed.ProviderId, new("exame", maria, null, null), Ct);

        // Assert
        everything.Items.Should().HaveCount(25);
        everything.HasMore.Should().BeTrue();
        everything.Items.Should().BeInDescendingOrder(i => i.UploadedAt);
        secondPage.Items.Should().HaveCount(6);
        secondPage.HasMore.Should().BeFalse();
        secondPage.Items.Last().Id.Should().Be(receita.Id);
        byKind.Items.Should().ContainSingle().Which.Should().Match<ExternalDocumentItem>(i => i.Id == receita.Id && i.PatientName == "Joana Souza");
        byName.Items.Should().ContainSingle().Which.Id.Should().Be(receita.Id);
        byPatient.Items.Should().HaveCount(25).And.OnlyContain(i => i.PatientId == maria);
        everything.Items.Should().OnlyContain(i => i.PatientArchived == false);

        // Act: archive Joana
        await db.Patients.Where(p => p.Id == joana).ExecuteUpdateAsync(s => s.SetProperty(p => p.ArchivedAt, Now), Ct);
        var afterArchive = await ExternalDocumentService.SearchAsync(db, seed.ProviderId, new("receita", null, null, null), Ct);

        // Assert
        afterArchive.Items.Should().ContainSingle().Which.PatientArchived.Should().BeTrue();
    }
}
