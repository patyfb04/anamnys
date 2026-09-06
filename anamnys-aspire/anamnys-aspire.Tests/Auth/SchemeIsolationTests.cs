using System.Net;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

[Collection(SchemeIsolationCollection.Name)]
public class SchemeIsolationTests(SchemeIsolationFixture fixture)
{
    [Fact]
    public async Task PhiEndpoint_WithNoCredential_Returns401()
    {
        // Arrange
        var client = fixture.CreateServerClient();

        // Act
        var response = await client.GetAsync("/api/phi/probe", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminEndpoint_WithNoCredential_Returns401()
    {
        // Arrange
        var client = fixture.CreateServerClient();

        // Act
        var response = await client.GetAsync("/api/admin/probe", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PhiEndpoint_WithOwnerRealmBearerToken_Returns401NotForbidden()
    {
        // Arrange — a genuine, valid token from the owners realm. It is not
        // forged and not expired; it is simply signed by the wrong realm.
        var client = fixture.CreateServerClient();
        var token = await KeycloakTestTokens.GetOwnerAccessTokenAsync(
            fixture.KeycloakBaseAddress, TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // Act
        var response = await client.GetAsync("/api/phi/probe", TestContext.Current.CancellationToken);

        // Assert — 401, never 403. A 403 would mean a registered scheme accepted
        // the issuer and only a policy stopped it, which is exactly the weaker
        // guarantee this design exists to avoid.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminEndpoint_WithProviderRealmBearerToken_Returns401NotForbidden()
    {
        // Arrange
        var client = fixture.CreateServerClient();
        var token = await KeycloakTestTokens.GetProviderAccessTokenAsync(
            fixture.KeycloakBaseAddress, TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // Act
        var response = await client.GetAsync("/api/admin/probe", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
