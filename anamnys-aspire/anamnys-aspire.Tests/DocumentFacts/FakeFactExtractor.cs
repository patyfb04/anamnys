using Anamnys.Server.DocumentFacts;

namespace Anamnys.Tests.DocumentFacts;

// Returns what extract gives for the pages it is handed (nothing by default); records the calls.
internal sealed class FakeFactExtractor(Func<IReadOnlyList<FactPage>, IReadOnlyList<FactCandidate>>? extract = null) : IFactExtractor
{
    public int Calls { get; private set; }
    public IReadOnlyList<FactPage>? LastPages { get; private set; }

    public Task<IReadOnlyList<FactCandidate>> ExtractAsync(IReadOnlyList<FactPage> pages, CancellationToken ct)
    {
        Calls++;
        LastPages = pages;
        return Task.FromResult(extract?.Invoke(pages) ?? []);
    }
}
