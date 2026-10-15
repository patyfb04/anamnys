using Anamnys.Server.Ai;
using Microsoft.Extensions.Options;
using Whisper.net;

namespace Anamnys.Server.LiveSession;

// Downloads the public ggml model on first use, verifies the pinned SHA-256 and keeps one
// WhisperFactory for the process (spec §4.4). Only the model comes in; nothing goes out.
public sealed class WhisperModelProvider(
    IOptions<LiveSessionOptions> options, IHttpClientFactory http, ILogger<WhisperModelProvider> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;

    public async Task<WhisperFactory> GetFactoryAsync(CancellationToken ct)
    {
        if (_factory is not null)
        {
            return _factory;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (_factory is not null)
            {
                return _factory;
            }
            var o = options.Value;
            if (o.ModelSha256 is not { Length: 64 })
            {
                throw new InvalidOperationException("LiveSession:ModelSha256 must be set to the model's SHA-256.");
            }
            Directory.CreateDirectory(o.ResolvedDirectory);
            var file = $"ggml-{o.Model}.bin";
            var path = Path.Combine(o.ResolvedDirectory, file);
            using (var client = http.CreateClient(nameof(WhisperModelProvider)))
            {
                await ModelFiles.EnsureAsync(client, new Uri(o.ModelBaseUrl, file), path, o.ModelSha256, logger, ct);
            }
            _factory = WhisperFactory.FromPath(path);
            return _factory;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static Task<bool> VerifyAsync(string path, string expectedSha256, CancellationToken ct) =>
        ModelFiles.VerifyAsync(path, expectedSha256, ct);

    public void Dispose() => _factory?.Dispose();
}
