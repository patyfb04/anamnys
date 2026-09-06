using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Anamnys.Server.Auth;

// The isolation tests need real tokens from clients that support the
// client_credentials grant, because directAccessGrantsEnabled is false on
// every real BFF client. anamnys-test-provider and anamnys-test-owner exist
// only to fill that gap, and they carry a literal, publicly-known secret so
// nobody mistakes them for a real credential. This guard makes sure they
// never survive into a non-Development environment.
public static class DevOnlyTestClientGuard
{
    private static readonly (string Realm, string ClientId)[] ForbiddenClients =
    [
        (Realms.Providers, "anamnys-test-provider"),
        (Realms.Owners, "anamnys-test-owner"),
    ];

    // The seeded local login used to prove the dev auth flow end-to-end
    // without an admin-API mutation. It is stripped from the realm JSON at
    // Docker build time (keycloak/strip-dev-seed.jq) unless INCLUDE_DEV_SEED
    // is set, which AppHost.cs only does in Development — this check is
    // defence in depth for if that strip ever regresses, not the primary
    // control, because Keycloak imports and activates this account before
    // the server ever runs this gate (see ForbiddenLocalhostOrigins below).
    private static readonly (string Realm, string Username)[] ForbiddenUsers =
    [
        (Realms.Providers, "dev.provider"),
    ];

    // Every realm whose clients might carry a localhost:* dev origin
    // (registered so each SPA's Vite proxy can complete the OIDC redirect
    // against its own dev-server port). Same defence-in-depth caveat as
    // ForbiddenUsers: Keycloak has already loaded these origins into a
    // running realm by the time this check can run.
    private static readonly string[] RealmsToCheckForLocalhostOrigins =
    [
        Realms.Providers,
        Realms.Patients,
        Realms.Owners,
    ];

    public static async Task EnsureNoDevOnlyTestClientsExistAsync(
        bool isDevelopment,
        string? adminUsername,
        string? adminPassword,
        string? keycloakAdminBaseAddress,
        CancellationToken cancellationToken)
    {
        if (isDevelopment)
        {
            return;
        }

        if (string.IsNullOrEmpty(adminUsername) || string.IsNullOrEmpty(adminPassword))
        {
            throw new InvalidOperationException(
                "Cannot verify that the dev-only Keycloak test clients (anamnys-test-provider, " +
                "anamnys-test-owner) are absent: KEYCLOAK_ADMIN_USERNAME/KEYCLOAK_ADMIN_PASSWORD are not " +
                "configured outside Development. Refusing to start rather than skip this check.");
        }

        // Deliberately not the "keycloak" named HttpClient the rest of the app
        // uses: that client's BaseAddress is the Aspire service-discovery
        // pseudo-scheme "https+http://keycloak", which only resolves under
        // Aspire orchestration (Services__keycloak__* configuration). Outside
        // that, every request against it throws, and this gate would then
        // refuse to start in every environment regardless of whether the
        // test clients exist. KEYCLOAK_ADMIN_BASE_ADDRESS is a plain,
        // explicit setting instead, so a missing one fails with a message
        // naming exactly what to configure.
        if (string.IsNullOrEmpty(keycloakAdminBaseAddress) ||
            !Uri.TryCreate(keycloakAdminBaseAddress, UriKind.Absolute, out var baseAddress))
        {
            throw new InvalidOperationException(
                "Cannot verify that the dev-only Keycloak test clients are absent: KEYCLOAK_ADMIN_BASE_ADDRESS " +
                "is not configured as an absolute URL outside Development. Configure it to Keycloak's own " +
                "base address (e.g. https://keycloak.internal:8443/) before starting the server here. " +
                "Refusing to start rather than skip this check.");
        }

        using var keycloakClient = new HttpClient { BaseAddress = baseAddress };

        string accessToken;
        try
        {
            using var tokenResponse = await keycloakClient.PostAsync(
                "realms/master/protocol/openid-connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["client_id"] = "admin-cli",
                    ["username"] = adminUsername,
                    ["password"] = adminPassword,
                }),
                cancellationToken);
            tokenResponse.EnsureSuccessStatusCode();

            var payload = await tokenResponse.Content.ReadFromJsonAsync<AdminTokenResponse>(cancellationToken);
            accessToken = payload?.AccessToken
                ?? throw new InvalidOperationException(
                    "Cannot verify that the dev-only Keycloak test clients are absent: the admin token " +
                    "response carried no access_token. Refusing to start rather than skip this check.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Cannot verify that the dev-only Keycloak test clients are absent: failed to obtain a " +
                "Keycloak admin token. Refusing to start rather than skip this check.", ex);
        }

        foreach (var (realm, clientId) in ForbiddenClients)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"admin/realms/{realm}/clients?clientId={clientId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await keycloakClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var clients = await response.Content.ReadFromJsonAsync<List<AdminClientSummary>>(cancellationToken);
            if (clients is { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Dev-only Keycloak test client '{clientId}' exists in realm '{realm}'. This client has " +
                    "a literal, publicly-known secret and must never exist outside Development. Remove it " +
                    "from the realm before starting the server in this environment.");
            }
        }

        foreach (var (realm, username) in ForbiddenUsers)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"admin/realms/{realm}/users?username={username}&exact=true");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await keycloakClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var users = await response.Content.ReadFromJsonAsync<List<AdminUserSummary>>(cancellationToken);
            if (users is { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Dev-only Keycloak user '{username}' exists in realm '{realm}'. This is an ordinary, " +
                    "interactively-loginable account seeded only to prove the dev auth flow, and must never " +
                    "exist outside Development. Remove it from the realm before starting the server in this " +
                    "environment.");
            }
        }

        foreach (var realm in RealmsToCheckForLocalhostOrigins)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"admin/realms/{realm}/clients");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await keycloakClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var clients = await response.Content.ReadFromJsonAsync<List<AdminClientDetail>>(cancellationToken);
            foreach (var client in clients ?? [])
            {
                var localhostRedirectUri = client.RedirectUris?
                    .FirstOrDefault(uri => uri.Contains("localhost", StringComparison.OrdinalIgnoreCase));
                if (localhostRedirectUri is not null)
                {
                    throw new InvalidOperationException(
                        $"Client '{client.ClientId}' in realm '{realm}' has a localhost redirect URI " +
                        $"('{localhostRedirectUri}'), registered for a SPA's local Vite dev-server proxy. " +
                        "This must never exist outside Development. Remove it from the realm before starting " +
                        "the server in this environment.");
                }

                // "+" is Keycloak's own wildcard ("same origins as redirectUris") and is
                // fine to leave alone; only a literal localhost entry is a problem.
                var localhostWebOrigin = client.WebOrigins?
                    .FirstOrDefault(origin => origin != "+" && origin.Contains("localhost", StringComparison.OrdinalIgnoreCase));
                if (localhostWebOrigin is not null)
                {
                    throw new InvalidOperationException(
                        $"Client '{client.ClientId}' in realm '{realm}' has a localhost web origin " +
                        $"('{localhostWebOrigin}'), registered for a SPA's local Vite dev-server proxy. " +
                        "This must never exist outside Development. Remove it from the realm before starting " +
                        "the server in this environment.");
                }
            }
        }
    }

    private sealed record AdminTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken);

    private sealed record AdminClientSummary(
        [property: JsonPropertyName("clientId")] string ClientId);

    private sealed record AdminUserSummary(
        [property: JsonPropertyName("username")] string Username);

    private sealed record AdminClientDetail(
        [property: JsonPropertyName("clientId")] string ClientId,
        [property: JsonPropertyName("redirectUris")] List<string>? RedirectUris,
        [property: JsonPropertyName("webOrigins")] List<string>? WebOrigins);
}
