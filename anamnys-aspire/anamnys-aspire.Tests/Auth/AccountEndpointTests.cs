using System.Net;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// /auth/{realm}/account (AuthEndpoints.cs) is how the SPAs reach Keycloak's account
// console (password, 2FA) without knowing Keycloak's public origin themselves.
[Collection(SharedAppHostCollection.Name)]
public class AccountEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");

    [Theory]
    [InlineData("provider", "anamnys-providers")]
    [InlineData("patient", "anamnys-patients")]
    public async Task AccountEndpoint_RedirectsToTheRealmsKeycloakAccountConsole(string segment, string realm)
    {
        // Arrange
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = ProviderBaseAddress };
        await WaitForProviderDevServerAsync(client, TestContext.Current.CancellationToken);

        // Act
        using var response = await client.GetAsync($"/auth/{segment}/account", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location;
        location.Should().NotBeNull();
        location!.IsAbsoluteUri.Should().BeTrue();
        location.AbsolutePath.Should().Be($"/realms/{realm}/account/");
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
