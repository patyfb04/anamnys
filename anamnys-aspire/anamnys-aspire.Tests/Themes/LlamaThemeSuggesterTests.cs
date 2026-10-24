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
        var codes = await suggester.SuggestAsync("Relata insônia.", Terms, ["sono", "trabalho", "trabalho_burnout"], TestContext.Current.CancellationToken);

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
        var blank = await suggester.SuggestAsync("  ", Terms, [], ct);
        var noTerms = await suggester.SuggestAsync("Relata insônia.", [], [], ct);

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
        var budget = LlamaThemeSuggester.TextBudget(4096, 600, 5);
        var none = LlamaThemeSuggester.TextBudget(700, 600, 5);

        // Assert
        budget.Should().Be(4096 - 600 - 5 - LlamaThemeSuggester.OutputTokens - LlamaThemeSuggester.Margin);
        none.Should().BeLessThanOrEqualTo(0);
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
