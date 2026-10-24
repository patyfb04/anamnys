using Anamnys.Server.Themes;

namespace Anamnys.Tests.Themes;

internal sealed class FakeThemeSuggester(Func<string, IReadOnlyList<ThemeTermOption>, IReadOnlyList<string>>? suggest = null) : IThemeSuggester
{
    public int Calls { get; private set; }
    public string? LastText { get; private set; }
    public IReadOnlyList<ThemeTermOption>? LastTerms { get; private set; }
    public IReadOnlyCollection<string>? LastExcluded { get; private set; }

    public Task<IReadOnlyList<string>> SuggestAsync(
        string text, IReadOnlyList<ThemeTermOption> terms, IReadOnlyCollection<string> excludedCodes, CancellationToken ct)
    {
        Calls++;
        LastText = text;
        LastTerms = terms;
        LastExcluded = excludedCodes;
        return Task.FromResult(suggest?.Invoke(text, terms) ?? []);
    }

    public Task WarmAsync(IReadOnlyList<ThemeTermOption> terms, CancellationToken ct) => Task.CompletedTask;
}
