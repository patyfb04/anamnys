using Anamnys.Server.Excerpts;
using FluentAssertions;

namespace Anamnys.Tests.Excerpts;

public class ExcerptLocatorTests
{
    private const string Text = "Paciente relata   insônia.\nDormiu mal   a semana toda. Relata INSÔNIA de novo.";

    [Fact]
    public void Locate_ExactQuote_ReturnsOffsets()
    {
        // Act
        var span = ExcerptLocator.Locate(Text, "Dormiu mal");

        // Assert
        Text[span!.Value.Start..span.Value.End].Should().Be("Dormiu mal");
    }

    [Fact]
    public void Locate_DifferentCase_Matches()
    {
        // Act
        var span = ExcerptLocator.Locate(Text, "paciente RELATA");

        // Assert
        Text[span!.Value.Start..span.Value.End].Should().Be("Paciente relata");
    }

    [Fact]
    public void Locate_WhitespaceRuns_AreCollapsedAndOffsetsAreInTheOriginalText()
    {
        // Act
        var span = ExcerptLocator.Locate(Text, "relata insônia. dormiu mal a semana");

        // Assert
        Text[span!.Value.Start..span.Value.End].Should().Be("relata   insônia.\nDormiu mal   a semana");
    }

    [Fact]
    public void Locate_QuoteWithSurroundingWhitespace_IsTrimmed()
    {
        // Act
        var span = ExcerptLocator.Locate(Text, "  dormiu mal \n");

        // Assert
        Text[span!.Value.Start..span.Value.End].Should().Be("Dormiu mal");
    }

    [Fact]
    public void Locate_MultipleOccurrences_ReturnsTheFirst()
    {
        // Act
        var span = ExcerptLocator.Locate(Text, "insônia");

        // Assert
        span!.Value.Start.Should().Be(Text.IndexOf("insônia", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("não consta no texto")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Locate_AbsentOrEmptyQuote_ReturnsNull(string? quote)
    {
        ExcerptLocator.Locate(Text, quote).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Locate_EmptySection_ReturnsNull(string? section)
    {
        ExcerptLocator.Locate(section, "x").Should().BeNull();
    }
}
