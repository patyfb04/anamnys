using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// Owners-realm registration is open, so a signed-in owners session is not by itself
// staff: /api/admin also requires a staff role in the token. Drives the seeded
// dev.pending (no realm roles, see keycloak/realms/anamnys-owners.json).
[Collection(SharedAppHostCollection.Name)]
public class PendingOwnerTests
{
    // Same fixed-port requirement as OwnerSessionCookieTests: only
    // http://localhost:5276 is a registered redirect origin for anamnys-admin-web.
    private static readonly Uri AdminBaseAddress = new("http://localhost:5276/");

    [Fact]
    public async Task AdminProbe_WithPendingOwnerSession_Returns403AndMeReportsNoRoles()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            AdminBaseAddress, "/admin/", "owner", "dev.pending@anamnys.local", "PendingStaff!2026Dev",
            TestContext.Current.CancellationToken);

        // Act
        using var meResponse = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"), TestContext.Current.CancellationToken);
        using var probeResponse = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/admin/probe"), TestContext.Current.CancellationToken);

        // Assert — authenticated (me is 200), but not permitted on the admin group.
        meResponse.StatusCode.Should().Be(HttpStatusCode.OK, "a pending account still signs in");
        using var me = JsonDocument.Parse(await meResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        me.RootElement.GetProperty("roles").EnumerateArray()
            .Select(r => r.GetString())
            .Should().NotContain(["owner", "support", "ops"]);
        probeResponse.StatusCode.Should().Be(
            HttpStatusCode.Forbidden, "a pending owners session is authenticated but holds no staff role");
    }

    [Fact]
    public async Task OwnerRegisterEndpoint_RendersKeycloaksRegistrationForm()
    {
        // Arrange
        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { BaseAddress = AdminBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/admin/", TestContext.Current.CancellationToken);

        // Act
        using var response = await client.GetAsync(
            "/auth/owner/register?returnUrl=/admin/", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert — with registrationAllowed false Keycloak answers with its error page instead.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("register.ftl", "Keycloakify embeds the rendered page id in kcContext");
    }
}
