namespace Anamnys.Server.Themes;

// Picks, from a closed list, the themes a note's text explicitly talks about. Returns term codes
// (0–5, distinct, each one of the given terms and none of the excluded codes). terms is the full
// active list (it shapes the cached prompt prefix); excludedCodes (already tagged, or removed by the
// provider) are kept out by the grammar only. Runs in-process only: the text never leaves the server.
public interface IThemeSuggester
{
    Task<IReadOnlyList<string>> SuggestAsync(
        string text, IReadOnlyList<ThemeTermOption> terms, IReadOnlyCollection<string> excludedCodes, CancellationToken ct);

    // Gets the suggester ready (model weights, cached prompt prefix) without running an inference, so
    // the first SuggestAsync does not pay for it. No-op when already current for terms.
    Task WarmAsync(IReadOnlyList<ThemeTermOption> terms, CancellationToken ct);
}
