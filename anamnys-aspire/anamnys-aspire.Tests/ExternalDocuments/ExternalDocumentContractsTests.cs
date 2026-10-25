using Anamnys.Server.ExternalDocuments;
using FluentAssertions;

namespace Anamnys.Tests.ExternalDocuments;

public class ExternalDocumentContractsTests
{
    private static readonly DateOnly Today = new(2026, 10, 25);

    private static ExternalDocumentUpload Upload(string kind = "laudo", DateOnly? date = null, string? description = null) =>
        new("a.pdf", [1], kind, date, description);

    [Fact]
    public void Validate_AllKindsAndOptionalFieldsAtTheirLimits_PassesValidation()
    {
        // Act
        var errors = ExternalDocumentKinds.All.Select(k => Upload(k, Today, new string('x', 200)).Validate(Today)).ToList();

        // Assert
        errors.Should().OnlyContain(e => e.Count == 0);
        ExternalDocumentKinds.All.Should().Equal("laudo", "receita", "exame", "encaminhamento", "relatorio_externo", "outro");
    }

    [Fact]
    public void Validate_BadKindFutureDateLongDescription_ReportsEachField()
    {
        // Act
        var errors = Upload("whatever", Today.AddDays(1), new string('x', 201)).Validate(Today);

        // Assert
        errors.Keys.Should().BeEquivalentTo("kind", "documentDate", "description");
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("laudo", true)]
    [InlineData("nope", false)]
    public void SearchValidate_Kind(string kind, bool valid)
    {
        // Act
        var errors = new ExternalDocumentSearchRequest(kind == "" ? null : kind, null, null, null).Validate();

        // Assert
        (errors.Count == 0).Should().Be(valid || kind == "");
    }

    [Fact]
    public void SearchValidate_LongNameAndBadPage_AreRejected()
    {
        // Act
        var errors = new ExternalDocumentSearchRequest(null, null, new string('x', 101), 0).Validate();

        // Assert
        errors.Keys.Should().BeEquivalentTo("name", "page");
    }

    [Theory]
    [InlineData("C:\\a\\b\\laudo.pdf", "laudo.pdf")]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("", "documento")]
    [InlineData("  \u0001  ", "documento")]
    public void DisplayName_StripsPathsAndControlCharacters(string raw, string expected)
    {
        // Act
        var name = ExternalDocumentService.DisplayName(raw);

        // Assert
        name.Should().Be(expected);
    }

    [Fact]
    public void DisplayName_VeryLongName_IsCapped()
    {
        // Act
        var name = ExternalDocumentService.DisplayName(new string('a', 500) + ".pdf");

        // Assert
        name.Length.Should().Be(200);
    }

    [Fact]
    public void ObjectKeyFor_UsesOnlyProviderAndDocumentIds()
    {
        // Arrange
        var (provider, document) = (Guid.NewGuid(), Guid.NewGuid());

        // Act
        var key = ExternalDocumentService.ObjectKeyFor(provider, document);

        // Assert
        key.Should().Be($"external/{provider}/{document}");
    }
}
