using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace Anamnys.Server.Integrity;

// The configured TSA over HTTP (design/specs/2026-11-03-integrity-chain-design.md §6). Only the SHA-256 of the head hash
// leaves the system.
public sealed class HttpTimestampAuthority(HttpClient http, IntegrityOptions options) : ITimestampAuthority
{
    internal const int MaxResponseBytes = 64 * 1024;
    private readonly X509Certificate2? _root = options.Root();

    public string Url => options.TsaUrl;

    public Task<byte[]> StampAsync(string headHash, CancellationToken ct) => StampImprintAsync(TimestampTokens.Imprint(headHash), ct);

    public Task<byte[]> StampDigestAsync(byte[] sha256Digest, CancellationToken ct) =>
        sha256Digest.Length == SHA256.HashSizeInBytes
            ? StampImprintAsync(sha256Digest, ct)
            : throw new ArgumentException("A SHA-256 digest has 32 bytes.", nameof(sha256Digest));

    public DateTimeOffset? VerifyDigest(byte[] token, byte[] sha256Digest) => TimestampTokens.VerifyImprint(token, sha256Digest, _root);

    // Only the imprint leaves the system; the token is checked against the configured root before it is returned.
    private async Task<byte[]> StampImprintAsync(byte[] imprint, CancellationToken ct)
    {
        if (_root is null)
        {
            throw new InvalidOperationException("No TSA root certificate is configured (Integrity:TsaRootCertificate).");
        }
        var request = Rfc3161TimestampRequest.CreateFromHash(
            imprint, HashAlgorithmName.SHA256, null, RandomNumberGenerator.GetBytes(16), true);
        using var content = new ByteArrayContent(request.Encode());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        using var message = new HttpRequestMessage(HttpMethod.Post, options.TsaUrl) { Content = content };
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var body = await ReadCappedAsync(response.Content, ct);
        // Status, nonce and message imprint; throws CryptographicException when any of them is wrong.
        request.ProcessResponse(body, out _);
        var token = TimestampTokens.TokenOf(body);
        if (TimestampTokens.VerifyImprint(token, imprint, _root) is null)
        {
            throw new CryptographicException("The timestamp token does not chain to the configured root.");
        }
        return token;
    }

    // At most MaxResponseBytes, whether or not the authority sends a Content-Length: nothing larger is ever parsed.
    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new CryptographicException("The TSA response is too large.");
        }
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new CryptographicException("The TSA response is too large.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public DateTimeOffset? Verify(byte[] token, string headHash) => TimestampTokens.Verify(token, headHash, _root);
}
