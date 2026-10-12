using Anamnys.Server.Ai;
using LLama;
using LLama.Common;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.NoteStructuring;

// Downloads the public GGUF parts on first use, verifies each pinned SHA-256 and keeps one set
// of weights for the process. Only the model comes in; nothing goes out.
public sealed class LlmModelProvider(
    IOptions<NoteStructuringOptions> options, IHttpClientFactory http, ILogger<LlmModelProvider> logger) : IDisposable
{
    private sealed record LoadedModel(LLamaWeights Weights, ModelParams Params);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LoadedModel? _model;

    public async Task<(LLamaWeights Weights, ModelParams Params)> GetAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _model) is { } loaded)
        {
            return (loaded.Weights, loaded.Params);
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (_model is { } raced)
            {
                return (raced.Weights, raced.Params);
            }
            var o = options.Value;
            if (o.Files.Count == 0 || o.Files.Any(f => f.Name is not { Length: > 0 } || f.Sha256 is not { Length: 64 }))
            {
                throw new InvalidOperationException("NoteStructuring:Files must list every model file with its SHA-256.");
            }
            Directory.CreateDirectory(o.ResolvedDirectory);
            using (var client = http.CreateClient(nameof(LlmModelProvider)))
            {
                foreach (var file in o.Files)
                {
                    await ModelFiles.EnsureAsync(
                        client, new Uri(o.ModelBaseUrl, file.Name), Path.Combine(o.ResolvedDirectory, file.Name), file.Sha256, logger, ct);
                }
            }
            logger.LogInformation("Loading LLM model {File}.", o.Files[0].Name);
            // llama.cpp finds the other split parts next to the first one.
            var parameters = new ModelParams(Path.Combine(o.ResolvedDirectory, o.Files[0].Name))
            {
                ContextSize = o.ContextSize,
                Threads = o.ResolvedThreads,
            };
            var weights = LLamaWeights.LoadFromFile(parameters);
            Volatile.Write(ref _model, new LoadedModel(weights, parameters));
            return (weights, parameters);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _model?.Weights.Dispose();
}
