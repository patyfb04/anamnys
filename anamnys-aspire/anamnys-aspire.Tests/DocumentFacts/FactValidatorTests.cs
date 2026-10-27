using Anamnys.Server.DocumentFacts;
using FluentAssertions;

namespace Anamnys.Tests.DocumentFacts;

public class FactValidatorTests
{
    [Theory]
    [InlineData("F43.1", "F43.1")]
    [InlineData("f43.1", "F43.1")]
    [InlineData("F431", "F43.1")]
    [InlineData("F40", "F40")]
    [InlineData(" CID-10 F33.1 ", "F33.1")]
    [InlineData("CID F33", "F33")]
    public void Normalize_Icd_AcceptsOneCode_AndWritesItWithItsDot(string value, string expected)
    {
        // Act
        var normalized = FactValidator.Normalize(FactKinds.Icd, value);

        // Assert
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("F43.12")]
    [InlineData("43.1")]
    [InlineData("F4")]
    [InlineData("F43.1 e F40.1")]
    [InlineData("Transtorno de estresse pós-traumático")]
    [InlineData("")]
    public void Normalize_Icd_RejectsAnythingElse(string value)
    {
        // Act
        var normalized = FactValidator.Normalize(FactKinds.Icd, value);

        // Assert
        normalized.Should().BeNull();
    }

    [Theory]
    [InlineData("14 de setembro de 2026", 2026, 9, 14)]
    [InlineData("São Paulo, 14 de setembro de 2026.", 2026, 9, 14)]
    [InlineData("1º de março de 2026", 2026, 3, 1)]
    [InlineData("05/10/2026", 2026, 10, 5)]
    [InlineData("5.10.26", 2026, 10, 5)]
    [InlineData("2026-10-05", 2026, 10, 5)]
    public void ParseDate_DatesAsBrazilianDocumentsWriteThem_AreRead(string text, int year, int month, int day)
    {
        // Act
        var date = FactValidator.ParseDate(text);

        // Assert
        date.Should().Be(new DateOnly(year, month, day));
    }

    [Theory]
    [InlineData("31/02/2026")]
    [InlineData("14 de setembrro de 2026")]
    [InlineData("setembro de 2026")]
    [InlineData("ontem")]
    public void Normalize_IssueDate_NotARealDay_IsRejected(string value)
    {
        // Act
        var normalized = FactValidator.Normalize(FactKinds.IssueDate, value);

        // Assert
        normalized.Should().BeNull();
    }

    [Fact]
    public void Normalize_IssueDate_KeepsTheDateAsWritten()
    {
        // Act
        var normalized = FactValidator.Normalize(FactKinds.IssueDate, "  14 de setembro   de 2026 ");

        // Assert
        normalized.Should().Be("14 de setembro de 2026");
    }

    [Theory]
    [InlineData("CRM-SP 000000")]
    [InlineData("CRM 123456/SP")]
    [InlineData("CRP 06/12345")]
    [InlineData("CRFa 2-00000")]
    [InlineData("CREFITO-3 12345-F")]
    [InlineData("COREN-SP 123.456")]
    public void Normalize_Registration_CouncilAcronymWithANumber_IsAccepted(string value)
    {
        // Act
        var normalized = FactValidator.Normalize(FactKinds.Registration, value);

        // Assert
        normalized.Should().Be(value);
    }

    [Theory]
    [InlineData("Psiquiatra - CRM-SP 000000", "CRM-SP 000000")]
    [InlineData("CRM-SP 000000 (fictício)", "CRM-SP 000000")]
    [InlineData("Fonoaudióloga - CRFa 2-00000.", "CRFa 2-00000")]
    public void Normalize_Registration_TextAroundIt_KeepsAcronymToNumber(string value, string expected)
    {
        // Act
        var normalized = FactValidator.Normalize(FactKinds.Registration, value);

        // Assert
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("CRM-SP")]
    [InlineData("123456")]
    [InlineData("CRM 12")]
    [InlineData("Criança 1234")]
    [InlineData("RG 12.345.678-9")]
    public void Normalize_Registration_WithoutAcronymOrNumber_IsRejected(string value)
    {
        // Act
        var normalized = FactValidator.Normalize(FactKinds.Registration, value);

        // Assert
        normalized.Should().BeNull();
    }

    [Theory]
    [InlineData(FactKinds.Issuer, "Dr. Ricardo Menezes Lobo")]
    [InlineData(FactKinds.Medication, "Sertralina 50 mg, 1 comprimido pela manhã")]
    [InlineData(FactKinds.Conclusion, "Transtorno de estresse pós-traumático")]
    public void Normalize_TextKinds_AcceptText(string kind, string value)
    {
        // Act
        var normalized = FactValidator.Normalize(kind, value);

        // Assert
        normalized.Should().Be(value);
    }

    [Theory]
    [InlineData(FactKinds.Issuer, "   ")]
    [InlineData(FactKinds.Medication, "50")]
    [InlineData("diagnosis", "Depressão")]
    public void Normalize_EmptyNumberOnlyOrUnknownKind_IsRejected(string kind, string value)
    {
        // Act
        var normalized = FactValidator.Normalize(kind, value);

        // Assert
        normalized.Should().BeNull();
    }

    [Fact]
    public void WithCitedIcd_CodeAfterCidInAQuote_BecomesAnIcdCandidate_OnceAndOnlyWhenNotNamed()
    {
        // Arrange
        FactCandidate[] candidates =
        [
            new("conclusion", "F43.1 - Transtorno de estresse pós-traumático", 1, "CID-10 F43.1 - Transtorno de estresse pós-traumático."),
            new("conclusion", "Fobia social", 2, "CID: F40.1 - Fobia social; CID10 F33."),
            new("icd", "f33", 2, "F33"),
            new("conclusion", "Melhora", 2, "Usa F43.1 sem a sigla; código F99.12 inválido"),
            new("medication", "Sertralina", 1, "CID-10 F43.1 de novo"),
        ];

        // Act
        var result = FactValidator.WithCitedIcd(candidates);

        // Assert
        result.Should().HaveCount(candidates.Length + 2);
        result.Skip(candidates.Length).Should().Equal(
            new FactCandidate("icd", "F43.1", 1, "CID-10 F43.1 - Transtorno de estresse pós-traumático."),
            new FactCandidate("icd", "F40.1", 2, "CID: F40.1 - Fobia social; CID10 F33."));
    }

    [Fact]
    public void Normalize_TooLong_IsRejected()
    {
        // Act
        var normalized = FactValidator.Normalize(FactKinds.Conclusion, new string('a', FactValidator.MaxValueLength + 1));

        // Assert
        normalized.Should().BeNull();
    }
}
