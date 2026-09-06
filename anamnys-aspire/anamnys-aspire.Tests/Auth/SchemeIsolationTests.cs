using System.Net;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

[Collection(SharedAppHostCollection.Name)]
public class SchemeIsolationTests(SharedAppHostFixture fixture)
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

    // Positive controls. Without these, all four tests above pass just as
    // well if OIDC metadata retrieval breaks for every realm, the
    // audience-anamnys-api mapper is removed, or someone deletes every scheme
    // from the phi/admin groups — all three produce a blanket 401 that looks
    // identical to correct wrong-realm rejection. These prove a matching
    // scheme on the same route actually accepts a token of this shape.
    [Fact]
    public async Task PhiEndpoint_WithProviderRealmBearerToken_ReturnsOk()
    {
        // Arrange
        var client = fixture.CreateServerClient();
        var token = await KeycloakTestTokens.GetProviderAccessTokenAsync(
            fixture.KeycloakBaseAddress, TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // Act
        var response = await client.GetAsync("/api/phi/probe", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminEndpoint_WithOwnerRealmBearerToken_ReturnsOk()
    {
        // Arrange
        var client = fixture.CreateServerClient();
        var token = await KeycloakTestTokens.GetOwnerAccessTokenAsync(
            fixture.KeycloakBaseAddress, TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // Act
        var response = await client.GetAsync("/api/admin/probe", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
