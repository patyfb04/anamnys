using Anamnys.Server.DocumentFacts;
using Anamnys.Server.NoteStructuring;
using FluentAssertions;

namespace Anamnys.Tests.DocumentFacts;

// Pure: never loads the model (the loader throws if it is reached).
public class LlamaFactExtractorTests
{
    [Fact]
    public async Task ExtractAsync_NoPageWithText_ReturnsEmptyWithoutModel()
    {
        // Arrange
        using var gate = new LlmGate();
        var extractor = new LlamaFactExtractor(_ => throw new InvalidOperationException("model must not be loaded"), new NoteStructuringOptions(), gate);

        // Act
        var facts = await extractor.ExtractAsync([new FactPage(1, "  \n"), new FactPage(2, "")], TestContext.Current.CancellationToken);

        // Assert
        facts.Should().BeEmpty();
    }

    [Fact]
    public void Pack_PagesInOrder_GroupedWithinTheBudget_AndAnOversizedPageAlone()
    {
        // Arrange
        FactPage[] pages = [new(1, "aaaa"), new(2, "bbb"), new(3, "cccccccccc"), new(4, "d"), new(5, "ee")];

        // Act
        var runs = LlamaFactExtractor.Pack(pages, 8, (_, text) => text.Length);

        // Assert
        runs.Select(r => r.Select(p => p.PageNumber).ToArray()).Should().BeEquivalentTo(
            [new[] { 1, 2 }, [3], [4, 5]], o => o.WithStrictOrdering());
    }

    [Fact]
    public void ContextFor_IsThePromptPlusTheOutputRoundedUp_WithinTheConfiguredSize()
    {
        // Act
        var small = LlamaFactExtractor.ContextFor(10, 16384);
        var medium = LlamaFactExtractor.ContextFor(2000, 16384);
        var capped = LlamaFactExtractor.ContextFor(40000, 16384);

        // Assert
        small.Should().Be(1536u);
        medium.Should().BeGreaterThanOrEqualTo((uint)(2000 + LlamaFactExtractor.OutputTokens + FactGrammar.MaxItems * LlamaFactExtractor.ForcedTokensPerItem));
        (medium % 256).Should().Be(0u);
        capped.Should().Be(16384u);
    }

    [Fact]
    public void TextBudget_IsTheContextLessTheFixedPromptOutputAndForcedJson()
    {
        // Act
        var budget = LlamaFactExtractor.TextBudget(16384, 500);

        // Assert
        budget.Should().Be(16384 - 500 - LlamaFactExtractor.OutputTokens - FactGrammar.MaxItems * LlamaFactExtractor.ForcedTokensPerItem - LlamaFactExtractor.Margin);
    }
}
