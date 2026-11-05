using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Anamnys.Server.Integrity;

// An RFC 3161 time stamping authority (design/specs/2026-11-03-integrity-chain-design.md §6): stamping sends only the
// SHA-256 of the chain head hash; verification checks a stored token against the configured root.
public interface ITimestampAuthority
{
    string Url { get; }

    // The DER TimeStampToken for this head hash, already verified against the root. Throws on any failure.
    Task<byte[]> StampAsync(string headHash, CancellationToken ct);

    // The token's genTime when it verifies for this head hash (signature, message imprint, signer chained to the root);
    // null otherwise, never an exception.
    DateTimeOffset? Verify(byte[] token, string headHash);
}

public static class TimestampTokens
{
    public const string TimeStampingEku = "1.3.6.1.5.5.7.3.8";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    // What is stamped: SHA-256 of the head hash as written (lowercase hex, UTF-8, no newline), so an outside party can
    // check it with `openssl ts -verify -data` on a file holding just the hash.
    public static byte[] Imprint(string headHash) => SHA256.HashData(Encoding.UTF8.GetBytes(headHash));

    public static DateTimeOffset? Verify(byte[] token, string headHash, X509Certificate2? root)
    {
        if (root is null || token.Length == 0)
        {
            return null;
        }
        try
        {
            if (!Rfc3161TimestampToken.TryDecode(token, out var decoded, out var consumed) || consumed != token.Length
                || decoded.TokenInfo.HashAlgorithmId.Value != Sha256Oid
                || !decoded.VerifySignatureForHash(Imprint(headHash), HashAlgorithmName.SHA256, out var signer, null)
                || signer is null)
            {
                return null;
            }
            using (signer)
            {
                var genTime = decoded.TokenInfo.Timestamp;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationTime = genTime.UtcDateTime;
                chain.ChainPolicy.VerificationTimeIgnored = false;
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid(TimeStampingEku));
                chain.ChainPolicy.ExtraStore.AddRange(decoded.AsSignedCms().Certificates);
                return chain.Build(signer) ? genTime : null;
            }
        }
        catch (Exception e) when (e is CryptographicException or AsnContentException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    // The TimeStampToken inside a TimeStampResp (SEQUENCE { status, timeStampToken }), as the authority encoded it.
    internal static byte[] TokenOf(byte[] response)
    {
        var outer = new AsnReader(response, AsnEncodingRules.BER).ReadSequence();
        outer.ReadEncodedValue();
        return outer.ReadEncodedValue().ToArray();
    }
}
