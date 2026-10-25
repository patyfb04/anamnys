using Anamnys.Server.NoteStructuring;
using Anamnys.Server.Themes;
using FluentAssertions;

namespace Anamnys.Tests.Themes;

// Pure: never loads the model (the loader throws if it is reached).
public class LlamaThemeSuggesterTests
{
    private static readonly IReadOnlyList<ThemeTermOption> Terms =
    [
        new(Guid.NewGuid(), "sono", "Sono", null),
        new(Guid.NewGuid(), "trabalho", "Trabalho", null),
        new(Guid.NewGuid(), "trabalho_burnout", "Burnout", "Trabalho"),
    ];

    private static LlamaThemeSuggester Unloadable() =>
        new(_ => throw new InvalidOperationException("model must not be loaded"), new NoteStructuringOptions());

    [Fact]
    public async Task SuggestAsync_EveryTermExcluded_ReturnsEmptyWithoutModel()
    {
        // Arrange
        using var suggester = Unloadable();

        // Act
        var codes = await suggester.SuggestAsync("Relata insônia.", Terms, ["sono", "trabalho", "trabalho_burnout"], false, TestContext.Current.CancellationToken);

        // Assert
        codes.Should().BeEmpty();
    }

    [Fact]
    public async Task SuggestAsync_BlankTextOrNoTerms_ReturnsEmptyWithoutModel()
    {
        // Arrange
        using var suggester = Unloadable();
        var ct = TestContext.Current.CancellationToken;

        // Act
        var blank = await suggester.SuggestAsync("  ", Terms, [], false, ct);
        var noTerms = await suggester.SuggestAsync("Relata insônia.", [], [], false, ct);

        // Assert
        blank.Should().BeEmpty();
        noTerms.Should().BeEmpty();
    }

    [Fact]
    public void AllowedCodes_ExcludedAndUnknownCodes_LeavesTheRest()
    {
        // Act
        var allowed = LlamaThemeSuggester.AllowedCodes(Terms, ["sono", "nao_existe"]);

        // Assert
        allowed.Should().BeEquivalentTo(["trabalho", "trabalho_burnout"]);
    }

    [Fact]
    public void TextBudget_SmallContext_IsWhatRemainsAfterPrefixSuffixOutputAndMargin()
    {
        // Act
        var codes = LlamaThemeSuggester.TextBudget(4096, 600, 5, withQuotes: false);
        var quotes = LlamaThemeSuggester.TextBudget(4096, 600, 5, withQuotes: true);
        var none = LlamaThemeSuggester.TextBudget(700, 600, 5, withQuotes: false);

        // Assert: the fast codes-only suggest keeps the pre-F-03 budget (160 output tokens).
        codes.Should().Be(4096 - 600 - 5 - 160 - LlamaThemeSuggester.Margin);
        quotes.Should().Be(4096 - 600 - 5 - 720 - LlamaThemeSuggester.Margin);
        LlamaThemeSuggester.OutputTokensFor(false).Should().Be(LlamaThemeSuggester.CodesOutputTokens);
        LlamaThemeSuggester.OutputTokensFor(true).Should().Be(LlamaThemeSuggester.QuotesOutputTokens);
        none.Should().BeLessThanOrEqualTo(0);
    }

    [Fact]
    public void TextBudget_WithItems_ReservesTheForcedJsonOfEachItem()
    {
        // Act
        var budget = LlamaThemeSuggester.TextBudget(4096, 600, 5, withQuotes: true, items: 10);

        // Assert
        budget.Should().Be(4096 - 600 - 5 - 720 - 10 * LlamaThemeSuggester.ForcedTokensPerItem - LlamaThemeSuggester.Margin);
    }

    [Fact]
    public void Options_SuggestionDefaults_AreSmallContextAndStructuringThreads()
    {
        // Arrange
        var options = new NoteStructuringOptions { Threads = 7 };

        // Act & Assert
        options.SuggestionContextSize.Should().Be(4096u);
        options.ResolvedSuggestionThreads.Should().Be(7);
        new NoteStructuringOptions { Threads = 7, SuggestionThreads = 3 }.ResolvedSuggestionThreads.Should().Be(3);
    }
}
