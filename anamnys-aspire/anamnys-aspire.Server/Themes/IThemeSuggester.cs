namespace Anamnys.Server.Themes;

// A theme the model named and the passage of the note it quoted for it (design/specs/
// 2026-10-24-excerpt-references-design.md §3). Quote is null when the model gave none (or it was cut);
// it is not trusted: the server locates it in the note sections before using it.
public sealed record ThemeCodeQuote(string Code, string? Quote);

// Picks, from a closed list, the themes a note's text explicitly talks about. Returns term codes
// (0–5, distinct, each one of the given terms and none of the excluded codes), each with the short
// passage that justifies it. terms is the full active list (it shapes the cached prompt prefix);
// excludedCodes (already tagged, or removed by the provider) are dropped from the parsed output.
// Runs in-process only: the text never leaves the server.
public interface IThemeSuggester
{
    // withQuotes false: codes only (quotes null), the fast mode of the suggest dialog; true: each code
    // with its quote (structuring proposals). Both share the cached prefix.
    Task<IReadOnlyList<ThemeCodeQuote>> SuggestAsync(
        string text, IReadOnlyList<ThemeTermOption> terms, IReadOnlyCollection<string> excludedCodes, bool withQuotes,
        CancellationToken ct);

    // For codes already chosen (the note's tags): asks the model only for the passage of each one.
    // terms is the full active list (same cached prefix); codes is a subset of it. Returns at most one
    // item per code, in any order; a code may be missing or have a null quote.
    Task<IReadOnlyList<ThemeCodeQuote>> QuoteAsync(
        string text, IReadOnlyList<ThemeTermOption> terms, IReadOnlyList<string> codes, CancellationToken ct);

    // Gets the suggester ready (model weights, cached prompt prefix) without running an inference, so
    // the first SuggestAsync does not pay for it. No-op when already current for terms.
    Task WarmAsync(IReadOnlyList<ThemeTermOption> terms, CancellationToken ct);
}
