using Anamnys.Server.Patients;

namespace Anamnys.Server.RecordingConsents;

// The signing and revocation links' tokens: 32 random bytes as base64url (43 characters), only their SHA-256 stored.
public static class ConsentTokens
{
    private const int TokenLength = 43;

    public static string NewToken() => PatientInvitationTokens.NewToken();

    public static byte[] Hash(string token) => PatientInvitationTokens.Hash(token);

    public static bool IsWellFormed(string? token) =>
        token is { Length: TokenLength } && token.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    public static Uri SignLink(Uri portalBaseUrl, string token) => new($"{portalBaseUrl.ToString().TrimEnd('/')}/consentimento/{token}");

    public static Uri RevokeLink(Uri portalBaseUrl, string token) =>
        new($"{portalBaseUrl.ToString().TrimEnd('/')}/consentimento/revogar/{token}");
}
