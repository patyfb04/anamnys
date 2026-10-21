using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using Anamnys.Tests.Patients;
using FluentAssertions;

namespace Anamnys.Tests.Documents;

[Collection(SharedAppHostCollection.Name)]
public class DocumentListTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> AddDocumentAsync(
        PatientSearchSeed seed, Guid patientId, DateTimeOffset createdAt, bool signed = false, Guid? supersededBy = null,
        string kind = "declaracao", Guid? providerId = null)
    {
        await using var db = seed.CreateDbContext();
        var isSigned = signed || supersededBy is not null;
        var document = new ClinicalDocument
        {
            Id = Guid.NewGuid(),
            PatientId = patientId,
            ProviderId = providerId ?? seed.ProviderId,
            Kind = kind,
            SignedAt = isSigned ? createdAt.AddMinutes(1) : null,
            SignedByCrp = isSigned ? "12345/06" : null,
            SignedCity = isSigned ? "São Paulo" : null,
            SignedByName = isSigned ? "Dra. Teste" : null,
            SignedPatientName = isSigned ? "Paciente Teste" : null,
            SupersededBy = supersededBy,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
        db.ClinicalDocuments.Add(document);
        await db.SaveChangesAsync(Ct);
        return document.Id;
    }

    [Theory]
    [InlineData("draft", new[] { "draft" })]
    [InlineData("signed", new[] { "signed" })]
    [InlineData("superseded", new[] { "superseded" })]
    [InlineData(null, new[] { "draft", "signed", "superseded" })]
    public async Task SearchAsync_StatusFilter_ReturnsMatchingDocuments(string? status, string[] expected)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var draft = await AddDocumentAsync(seed, patient, Now);
        await AddDocumentAsync(seed, patient, Now.AddMinutes(-1), signed: true);
        await AddDocumentAsync(seed, patient, Now.AddMinutes(-2), supersededBy: draft);
        await using var db = seed.CreateDbContext();

        // Act
        var response = await DocumentList.SearchAsync(db, seed.ProviderId, new DocumentSearchRequest(status, null, null, null), Ct);

        // Assert
        response.Items.Select(i => i.Status).Should().BeEquivalentTo(expected);
        response.Items.Should().OnlyContain(i => i.Title == "Declaração" && i.PatientName == "Ana Silva");
    }

    [Fact]
    public async Task SearchAsync_PatientAndName_Filter()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var ana = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var bia = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        await AddDocumentAsync(seed, ana, Now);
        await AddDocumentAsync(seed, bia, Now);
        await using var db = seed.CreateDbContext();

        // Act
        var byPatient = await DocumentList.SearchAsync(db, seed.ProviderId, new DocumentSearchRequest(null, bia, null, null), Ct);
        var byName = await DocumentList.SearchAsync(db, seed.ProviderId, new DocumentSearchRequest(null, null, "na sil", null), Ct);

        // Assert
        byPatient.Items.Should().ContainSingle().Which.PatientName.Should().Be("Bia Souza");
        byName.Items.Should().ContainSingle().Which.PatientId.Should().Be(ana);
    }

    [Fact]
    public async Task SearchAsync_MoreThanAPage_PagesNewestFirst()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        for (var i = 0; i < DocumentList.PageSize + 2; i++)
        {
            await AddDocumentAsync(seed, patient, Now.AddMinutes(-i));
        }
        await using var db = seed.CreateDbContext();

        // Act
        var first = await DocumentList.SearchAsync(db, seed.ProviderId, new DocumentSearchRequest(null, null, null, 1), Ct);
        var second = await DocumentList.SearchAsync(db, seed.ProviderId, new DocumentSearchRequest(null, null, null, 2), Ct);

        // Assert
        first.Items.Should().HaveCount(DocumentList.PageSize);
        first.HasMore.Should().BeTrue();
        first.Items.Select(i => i.CreatedAt).Should().BeInDescendingOrder();
        first.Items[0].CreatedAt.Should().Be(Now);
        second.Items.Should().HaveCount(2);
        second.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_OtherProvidersDocuments_NeverReturned()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var stranger = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await AddDocumentAsync(seed, stranger, Now, providerId: other);
        await using var db = seed.CreateDbContext();

        // Act
        var all = await DocumentList.SearchAsync(db, seed.ProviderId, new DocumentSearchRequest(null, null, null, null), Ct);
        var byPatient = await DocumentList.SearchAsync(db, seed.ProviderId, new DocumentSearchRequest(null, stranger, null, null), Ct);

        // Assert
        all.Items.Should().BeEmpty();
        byPatient.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("open", null, "status")]
    [InlineData(null, 0, "page")]
    [InlineData(null, DocumentSearchRequest.MaxPage + 1, "page")]
    public void Validate_Invalid_ReportsField(string? status, int? page, string field)
    {
        // Act
        var errors = new DocumentSearchRequest(status, null, null, page).Validate();

        // Assert
        errors.Should().ContainKey(field);
    }

    [Fact]
    public void Validate_NameOverLimit_ReportsName()
    {
        // Act
        var errors = new DocumentSearchRequest(null, null, new string('a', DocumentSearchRequest.MaxName + 1), null).Validate();

        // Assert
        errors.Should().ContainKey("name");
    }

    [Fact]
    public void Validate_ValidRequest_HasNoErrors()
    {
        // Act
        var errors = new DocumentSearchRequest("signed", null, "Ana", 1).Validate();

        // Assert
        errors.Should().BeEmpty();
    }
}
