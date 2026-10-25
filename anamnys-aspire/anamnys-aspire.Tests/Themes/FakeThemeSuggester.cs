using Anamnys.Server.Themes;

namespace Anamnys.Tests.Themes;

// suggest picks the codes; quotes (code -> quote) is what the "model" quotes for each code, for both
// SuggestAsync and QuoteAsync. A code without a quote comes back with a null quote from SuggestAsync
// and is left out by QuoteAsync.
internal sealed class FakeThemeSuggester(
    Func<string, IReadOnlyList<ThemeTermOption>, IReadOnlyList<string>>? suggest = null,
    IReadOnlyDictionary<string, string>? quotes = null) : IThemeSuggester
{
    public int Calls { get; private set; }
    public int QuoteCalls { get; private set; }
    public string? LastText { get; private set; }
    public IReadOnlyList<ThemeTermOption>? LastTerms { get; private set; }
    public IReadOnlyCollection<string>? LastExcluded { get; private set; }
    public bool? LastWithQuotes { get; private set; }
    public IReadOnlyList<string>? LastQuoteCodes { get; private set; }
    public Exception? QuoteFailure { get; init; }

    public Task<IReadOnlyList<ThemeCodeQuote>> SuggestAsync(
        string text, IReadOnlyList<ThemeTermOption> terms, IReadOnlyCollection<string> excludedCodes, bool withQuotes, CancellationToken ct)
    {
        Calls++;
        LastText = text;
        LastTerms = terms;
        LastExcluded = excludedCodes;
        LastWithQuotes = withQuotes;
        IReadOnlyList<ThemeCodeQuote> items = (suggest?.Invoke(text, terms) ?? [])
            .Select(c => new ThemeCodeQuote(c, withQuotes ? quotes?.GetValueOrDefault(c) : null))
            .ToList();
        return Task.FromResult(items);
    }

    public Task<IReadOnlyList<ThemeCodeQuote>> QuoteAsync(
        string text, IReadOnlyList<ThemeTermOption> terms, IReadOnlyList<string> codes, CancellationToken ct)
    {
        QuoteCalls++;
        LastText = text;
        LastQuoteCodes = codes;
        if (QuoteFailure is not null)
        {
            throw QuoteFailure;
        }
        IReadOnlyList<ThemeCodeQuote> items = codes
            .Where(c => quotes?.ContainsKey(c) == true)
            .Select(c => new ThemeCodeQuote(c, quotes![c]))
            .ToList();
        return Task.FromResult(items);
    }

    public Task WarmAsync(IReadOnlyList<ThemeTermOption> terms, CancellationToken ct) => Task.CompletedTask;
}
