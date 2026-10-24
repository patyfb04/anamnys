using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Anamnys.Tests.Auth;

// The seeded owners-realm users cover only `support` (dev.owner) and no role (dev.pending), so
// sessions for `ops` and `owner` are made on demand: the user is created (or reused) through
// Keycloak's admin API with exactly one realm role, then logs in through the real admin-app flow.
internal static class StaffSessions
{
    private const string Realm = "anamnys-owners";
    private const string Password = "TestStaff!2026Dev";
    private static readonly Uri AdminBaseAddress = new("http://localhost:5276/");

    public static Task<BrowserSession> LoginAsync(SharedAppHostFixture fixture, string role, CancellationToken ct) =>
        role == "support"
            ? BrowserSession.LoginAsync(AdminBaseAddress, "/admin/", "owner", "dev.owner@anamnys.local", "SupportStaff!2026Dev", ct)
            : LoginWithRoleAsync(fixture, role, ct);

    private static async Task<BrowserSession> LoginWithRoleAsync(SharedAppHostFixture fixture, string role, CancellationToken ct)
    {
        var username = $"test.{role}";
        var email = $"{username}@anamnys.local";
        using var keycloak = KeycloakTestHttpClient.Create(fixture.KeycloakBaseAddress);
        keycloak.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync(keycloak, fixture, ct));

        var userId = await FindUserAsync(keycloak, username, ct);
        if (userId is null)
        {
            using var create = await keycloak.PostAsJsonAsync($"admin/realms/{Realm}/users", new
            {
                username,
                email,
                firstName = "Test",
                lastName = role,
                enabled = true,
                emailVerified = true,
                credentials = new[] { new { type = "password", value = Password, temporary = false } },
            }, ct);
            create.EnsureSuccessStatusCode();
            userId = await FindUserAsync(keycloak, username, ct);

            using var roleResponse = await keycloak.GetAsync($"admin/realms/{Realm}/roles/{role}", ct);
            roleResponse.EnsureSuccessStatusCode();
            var representation = await roleResponse.Content.ReadFromJsonAsync<RoleRepresentation>(ct);
            using var map = await keycloak.PostAsJsonAsync(
                $"admin/realms/{Realm}/users/{userId}/role-mappings/realm", new[] { representation }, ct);
            map.EnsureSuccessStatusCode();
        }

        return await BrowserSession.LoginAsync(AdminBaseAddress, "/admin/", "owner", email, Password, ct);
    }

    private static async Task<string> AdminTokenAsync(HttpClient keycloak, SharedAppHostFixture fixture, CancellationToken ct)
    {
        using var response = await keycloak.PostAsync(
            "realms/master/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = fixture.KeycloakAdminUsername,
                ["password"] = fixture.KeycloakAdminPassword,
            }), ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        return payload!.AccessToken;
    }

    private static async Task<string?> FindUserAsync(HttpClient keycloak, string username, CancellationToken ct)
    {
        using var response = await keycloak.GetAsync($"admin/realms/{Realm}/users?username={username}&exact=true", ct);
        response.EnsureSuccessStatusCode();
        var users = await response.Content.ReadFromJsonAsync<List<UserSummary>>(ct);
        return users is { Count: > 0 } ? users[0].Id : null;
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

    private sealed record UserSummary(string Id);

    private sealed record RoleRepresentation(string Id, string Name);
}
