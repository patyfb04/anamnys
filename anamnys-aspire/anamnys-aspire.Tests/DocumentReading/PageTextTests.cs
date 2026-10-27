using Anamnys.Server.DocumentReading;
using FluentAssertions;

namespace Anamnys.Tests.DocumentReading;

public class PageTextTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("  \n\t . , ; - ", 0)]
    [InlineData("CID F33.1", 7)]
    [InlineData("Ação clínica", 11)]
    public void UsefulCharacters_CountsLettersAndDigitsOnly(string text, int expected)
    {
        // Arrange
        // Act
        var count = PageText.UsefulCharacters(text);

        // Assert
        count.Should().Be(expected);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("- 1 -", true)]
    [InlineData("Página 1 de 2 ....", true)]
    [InlineData("Paciente em acompanhamento psiquiátrico.", false)]
    public void NeedsOcr_FewerUsefulCharactersThanTheThreshold_IsAScan(string text, bool scanned)
    {
        // Arrange
        // Act
        var needsOcr = PageText.NeedsOcr(text, 20);

        // Assert
        needsOcr.Should().Be(scanned);
    }

    [Fact]
    public void Normalize_UnifiesLineBreaks_DropsControlCharactersTrailingSpacesAndExtraBlankLines()
    {
        // Arrange
        var raw = "\r\n  \nLinha um  \r\nLinha\0 dois\t\n\n\n\nLinha três\r\n\r\n";

        // Act
        var text = PageText.Normalize(raw);

        // Assert
        text.Should().Be("Linha um\nLinha dois\n\nLinha três");
    }
}
