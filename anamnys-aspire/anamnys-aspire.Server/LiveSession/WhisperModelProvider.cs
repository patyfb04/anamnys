using System.Security.Cryptography;
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
            var path = Path.Combine(o.ResolvedDirectory, $"ggml-{o.Model}.bin");
            if (!File.Exists(path) || !await VerifyAsync(path, o.ModelSha256, ct))
            {
                await DownloadAsync(new Uri(o.ModelBaseUrl, $"ggml-{o.Model}.bin"), path, o.ModelSha256, ct);
            }
            _factory = WhisperFactory.FromPath(path);
            return _factory;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static async Task<bool> VerifyAsync(string path, string expectedSha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        return string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadAsync(Uri url, string path, string sha256, CancellationToken ct)
    {
        logger.LogInformation("Downloading Whisper model {Url}.", url);
        var temp = path + ".download";
        using (var client = http.CreateClient(nameof(WhisperModelProvider)))
        await using (var source = await client.GetStreamAsync(url, ct))
        await using (var target = File.Create(temp))
        {
            await source.CopyToAsync(target, ct);
        }
        if (!await VerifyAsync(temp, sha256, ct))
        {
            File.Delete(temp);
            throw new InvalidOperationException("Downloaded Whisper model does not match LiveSession:ModelSha256.");
        }
        File.Move(temp, path, overwrite: true);
    }

    public void Dispose() => _factory?.Dispose();
}
