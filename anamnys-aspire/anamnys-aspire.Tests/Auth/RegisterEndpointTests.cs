using System.Net;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// /auth/provider/register (AuthEndpoints.cs) reuses the ordinary login challenge but
// marks it so AuthenticationSetup.cs's OnRedirectToIdentityProvider sends the browser to
// Keycloak's registration form instead of its login form. This only proves our own
// redirect-building code took the right branch — it does not depend on
// anamnys-providers.json's registrationAllowed at all, so it stays green regardless of
// that realm setting.
[Collection(SharedAppHostCollection.Name)]
public class RegisterEndpointTests
{
    // Same fixed-port requirement as OwnerSessionCookieTests: the server derives its
    // redirect_uri from the Host header, and only the provider app's registered
    // redirect URI (keycloak/realms/anamnys-providers.json) is accepted.
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");

    [Fact]
    public async Task RegisterEndpoint_RedirectsToKeycloaksRegistrationsEndpoint_NotLogin()
    {
        // Arrange
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = ProviderBaseAddress };
        await WaitForProviderDevServerAsync(client, TestContext.Current.CancellationToken);

        // Act
        using var response = await client.GetAsync(
            "/auth/provider/register?returnUrl=/provider/", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location;
        location.Should().NotBeNull();
        location!.AbsolutePath.Should().Contain(
            "/protocol/openid-connect/registrations",
            $"the register endpoint must redirect to Keycloak's registration form, not its login form. Got: {location}");
        location.AbsolutePath.Should().NotContain("/protocol/openid-connect/auth");
    }

    [Fact]
    public async Task LoginEndpoint_StillRedirectsToKeycloaksLoginEndpoint_Unaffected()
    {
        // Arrange — the register endpoint's marker must not leak onto the ordinary
        // login challenge it shares an OIDC scheme with.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = ProviderBaseAddress };
        await WaitForProviderDevServerAsync(client, TestContext.Current.CancellationToken);

        // Act
        using var response = await client.GetAsync(
            "/auth/provider/login?returnUrl=/provider/", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location;
        location.Should().NotBeNull();
        location!.AbsolutePath.Should().Contain("/protocol/openid-connect/auth");
    }

    private static async Task WaitForProviderDevServerAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/provider/", timeout.Token);
                if ((int)response.StatusCode < 500)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        }
    }
}
