using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Anamnys.Server.Integrity;

// design/specs/2026-11-03-integrity-chain-design.md §6. TsaRootCertificate is the PEM of the authority's root: a token is
// stored and accepted only when its signer chains to it. Outside Development both are required; in Development an empty
// root turns the daily anchoring off (signing and verification still work).
public sealed class IntegrityOptions
{
    public const string DefaultTsaUrl = "https://freetsa.org/tsr";

    public string TsaUrl { get; set; } = DefaultTsaUrl;
    public string TsaRootCertificate { get; set; } = "";

    public bool CanStamp => !string.IsNullOrWhiteSpace(TsaRootCertificate);

    // Null when the PEM is empty; throws when it is present but not a certificate.
    public X509Certificate2? Root() =>
        CanStamp ? X509Certificate2.CreateFromPem(TsaRootCertificate) : null;

    internal static string? Problem(IntegrityOptions options, bool isDevelopment)
    {
        if (!Uri.TryCreate(options.TsaUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return "Integrity:TsaUrl must be an absolute http(s) URL of an RFC 3161 time stamping authority.";
        }
        if (!options.CanStamp)
        {
            return isDevelopment
                ? null
                : "Integrity:TsaRootCertificate (PEM) must be set outside Development (AppHost parameter integrity-tsa-root-certificate).";
        }
        try
        {
            using var root = options.Root();
        }
        catch (CryptographicException)
        {
            return "Integrity:TsaRootCertificate is not a PEM certificate.";
        }
        return null;
    }
}
