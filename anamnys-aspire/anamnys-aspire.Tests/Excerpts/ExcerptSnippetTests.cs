using Anamnys.Server.Excerpts;
using FluentAssertions;

namespace Anamnys.Tests.Excerpts;

public class ExcerptSnippetTests
{
    [Fact]
    public void Snippet_ShortSection_IsTheWholeTextOnOneLine()
    {
        // Act & Assert
        ExcerptService.Snippet("Linha um.\n\nLinha  dois.", null, null).Should().Be("Linha um. Linha dois.");
    }

    [Fact]
    public void Snippet_LongSection_IsCutWithEllipsis()
    {
        // Act
        var snippet = ExcerptService.Snippet(new string('a', 300), null, null);

        // Assert
        snippet.Should().HaveLength(160).And.EndWith("…");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(290)]
    public void Snippet_ExactRange_IsCentredAndAtMost160(int at)
    {
        // Arrange
        var text = new string('x', at) + "QUOTE" + new string('y', 300 - at);

        // Act
        var snippet = ExcerptService.Snippet(text, at, at + 5);

        // Assert
        snippet.Length.Should().BeLessThanOrEqualTo(160);
        snippet.Should().Contain("QUOTE");
        snippet.StartsWith('…').Should().Be(at > 0);
        snippet.EndsWith('…').Should().Be(at < 290);
    }

    [Fact]
    public void Snippet_RangeLongerThanMax_IsTheRangeCut()
    {
        // Arrange
        var text = "pre " + new string('q', 200) + " pos";

        // Act
        var snippet = ExcerptService.Snippet(text, 4, 204);

        // Assert
        snippet.Should().HaveLength(160).And.StartWith("qqq").And.EndWith("…");
    }
}
