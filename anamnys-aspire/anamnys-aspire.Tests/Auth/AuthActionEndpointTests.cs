using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// /auth/{realm}/action/{action} (AuthEndpoints.cs) starts a Keycloak
// application-initiated action (password, 2FA, email) for the signed-in user.
[Collection(SharedAppHostCollection.Name)]
public class AuthActionEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");

    [Theory]
    [InlineData("UPDATE_PASSWORD", "login-update-password")]
    [InlineData("CONFIGURE_TOTP", "login-config-totp")]
    [InlineData("UPDATE_EMAIL", "update-email")]
    public async Task ActionEndpoint_WithSessionAndAllowedAction_OpensThatKeycloakActionPage(string action, string expectedPageId)
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026",
            TestContext.Current.CancellationToken);

        // Act — the BFF's 302 goes to Keycloak's authorization endpoint (kc_action rides in
        // the pushed authorization request, so it is not visible in the URL); following it
        // with the live SSO session lands on the page for that action.
        using var redirect = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/auth/provider/action/{action}?returnUrl=/provider/account/security"),
            TestContext.Current.CancellationToken);
        redirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        redirect.Headers.Location!.AbsolutePath.Should().Be("/realms/anamnys-providers/protocol/openid-connect/auth");
        using var page = await session.SendFollowingRedirectsAsync(
            new HttpRequestMessage(HttpMethod.Get, redirect.Headers.Location), TestContext.Current.CancellationToken);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        var pageId = Regex.Match(html, "\"pageId\"\\s*:\\s*\"([^\"]+)\"");
        pageId.Success.Should().BeTrue($"Keycloak must render a themed page with a kcContext. Body: {html}");
        pageId.Groups[1].Value.Should().Be(expectedPageId);
    }

    [Fact]
    public async Task ActionEndpoint_WithSessionAndUnknownAction_Returns400()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026",
            TestContext.Current.CancellationToken);

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/auth/provider/action/delete_account"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ActionEndpoint_WithoutSession_Returns401()
    {
        // Arrange
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", TestContext.Current.CancellationToken);

        // Act
        using var response = await client.GetAsync(
            "/auth/provider/action/UPDATE_PASSWORD", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
