namespace Anamnys.Server.DocumentFacts;

// Reads, from the pages of one document, the facts it declares (up to FactGrammar.MaxItems per model run),
// each with its page and the sentence it was read from. In process only: the text never leaves the server.
// The output is not trusted (FactService validates and locates every candidate).
public interface IFactExtractor
{
    Task<IReadOnlyList<FactCandidate>> ExtractAsync(IReadOnlyList<FactPage> pages, CancellationToken ct);
}

// DocumentFacts:Extractor = "none": nothing is extracted (the test AppHost, where the model must never load).
public sealed class NoFactExtractor : IFactExtractor
{
    public Task<IReadOnlyList<FactCandidate>> ExtractAsync(IReadOnlyList<FactPage> pages, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FactCandidate>>([]);
}
