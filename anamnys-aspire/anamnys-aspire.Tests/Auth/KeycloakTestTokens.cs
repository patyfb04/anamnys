using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Anamnys.Tests.Auth;

// The real BFF clients disable directAccessGrantsEnabled, so these tests
// cannot ask Keycloak for a token with a password. anamnys-test-provider and
// anamnys-test-owner are dedicated service-account clients that exist only in
// this dev configuration, gated out of every other environment by
// DevOnlyTestClientGuard.
internal static class KeycloakTestTokens
{
    public static Task<string> GetProviderAccessTokenAsync(Uri keycloakBaseAddress, CancellationToken cancellationToken) =>
        GetAsync(keycloakBaseAddress, "anamnys-providers", "anamnys-test-provider", cancellationToken);

    public static Task<string> GetOwnerAccessTokenAsync(Uri keycloakBaseAddress, CancellationToken cancellationToken) =>
        GetAsync(keycloakBaseAddress, "anamnys-owners", "anamnys-test-owner", cancellationToken);

    private static async Task<string> GetAsync(
        Uri keycloakBaseAddress, string realm, string clientId, CancellationToken cancellationToken)
    {
        using var client = KeycloakTestHttpClient.Create(keycloakBaseAddress);
        using var response = await client.PostAsync(
            $"realms/{realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = "test-only-not-a-secret",
            }),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        return payload!.AccessToken;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken);
}
