using System.Security.Cryptography;

namespace Anamnys.Server.Ai;

// Public model files fetched on first use and verified against a pinned SHA-256 before they
// are used. Only the model comes in; nothing goes out.
public static class ModelFiles
{
    public static async Task<bool> VerifyAsync(string path, string sha256, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        return string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase);
    }

    // No-op when the file is already in place and verifies. Otherwise downloads to a temporary
    // file, verifies it and moves it into place; the temporary file never survives a failure.
    public static async Task EnsureAsync(HttpClient client, Uri url, string path, string sha256, ILogger logger, CancellationToken ct)
    {
        if (File.Exists(path) && await VerifyAsync(path, sha256, ct))
        {
            return;
        }
        logger.LogInformation("Downloading model file {Url}.", url);
        var temp = path + ".download";
        try
        {
            await using (var source = await client.GetStreamAsync(url, ct))
            await using (var target = File.Create(temp))
            {
                await source.CopyToAsync(target, ct);
            }
            if (!await VerifyAsync(temp, sha256, ct))
            {
                throw new InvalidOperationException($"Downloaded model file {Path.GetFileName(path)} does not match its pinned SHA-256.");
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            // A failed cleanup must not hide the original failure.
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
