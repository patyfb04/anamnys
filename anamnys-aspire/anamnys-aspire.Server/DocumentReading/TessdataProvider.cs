using Anamnys.Server.Ai;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.DocumentReading;

// Downloads por.traineddata on first use and verifies the pinned SHA-256, like the speech and language
// models. Only the data file comes in; nothing goes out.
public sealed class TessdataProvider(
    IOptions<DocumentReadingOptions> options, IHttpClientFactory http, ILogger<TessdataProvider> logger)
{
    public const string Language = "por";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _directory;

    public async Task<string> GetDirectoryAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _directory) is { } ready)
        {
            return ready;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (_directory is { } raced)
            {
                return raced;
            }
            var o = options.Value;
            if (o.TessdataSha256 is not { Length: 64 })
            {
                throw new InvalidOperationException("DocumentReading:TessdataSha256 must hold the SHA-256 of por.traineddata.");
            }
            var directory = o.ResolvedTessdataDirectory;
            Directory.CreateDirectory(directory);
            using (var client = http.CreateClient(nameof(TessdataProvider)))
            {
                await ModelFiles.EnsureAsync(
                    client, o.TessdataUrl, Path.Combine(directory, $"{Language}.traineddata"), o.TessdataSha256, logger, ct);
            }
            Volatile.Write(ref _directory, directory);
            return directory;
        }
        finally
        {
            _gate.Release();
        }
    }
}
