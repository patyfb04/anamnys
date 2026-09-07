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

    // Bearer is registered but not mounted. Program.cs puts only the cookie
    // schemes on /api/phi and /api/admin, because AnamnysClaims.LocalId is
    // minted on the OIDC handler alone and CurrentUser.LocalId — the one
    // sanctioned way to resolve a local row id (spec §5) — therefore throws for
    // any bearer principal. These two assert that decision behaviourally with
    // the *matching* realm's token: if a token that would be accepted on issuer
    // and audience is still rejected, no bearer credential of any realm gets in.
    //
    // When the spec §10.3 mobile client ships, these are the tests that must
    // change — and they should only change in the same commit that mints LocalId
    // on JwtBearerEvents.OnTokenValidated. Note that the anamnys-test-* clients
    // used here are service accounts with no email claim, which
    // FirstLoginProvisioner requires, so provisioning on bearer is not a
    // drop-in: it needs its own thought about service-account principals.
    //
    // Wrong-realm-yields-401-not-403 is asserted on the credential type that is
    // actually mounted, in OwnerSessionCookieTests, with a positive control.
    [Fact]
    public async Task PhiEndpoint_WithProviderRealmBearerToken_Returns401BecauseBearerIsNotMounted()
    {
        // Arrange — a genuine providers-realm token with aud: anamnys-api.
        var client = fixture.CreateServerClient();
        var token = await KeycloakTestTokens.GetProviderAccessTokenAsync(
            fixture.KeycloakBaseAddress, TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // Act
        var response = await client.GetAsync("/api/phi/probe", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminEndpoint_WithOwnerRealmBearerToken_Returns401BecauseBearerIsNotMounted()
    {
        // Arrange
        var client = fixture.CreateServerClient();
        var token = await KeycloakTestTokens.GetOwnerAccessTokenAsync(
            fixture.KeycloakBaseAddress, TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // Act
        var response = await client.GetAsync("/api/admin/probe", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
