using System.Net;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// Asserts the session cookie carries no token material: SessionStore/RedisTicketStore
// exist precisely to keep the access and refresh tokens server-side, and nothing else
// would notice if that were silently bypassed. Drives a genuine owners-realm login (the
// seeded `dev.owner`, see keycloak/realms/anamnys-owners.json) and inspects the real
// `__Host-anamnys-owner` cookie.
[Collection(SharedAppHostCollection.Name)]
public class OwnerSessionCookieTests
{
    // The admin dev-server origin, not the server's own test port: the server builds the
    // OIDC redirect_uri from the Host header, and only http://localhost:5276 is registered
    // for anamnys-admin-web.
    private static readonly Uri AdminBaseAddress = new("http://localhost:5276/");

    [Fact]
    public async Task OwnerLogin_SessionCookie_CarriesNoTokenMaterialAndIsScopedToItsOwnScheme()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            AdminBaseAddress, "/admin/", "owner", "dev.owner@anamnys.local", "SupportStaff!2026Dev",
            TestContext.Current.CancellationToken);

        // Act
        var hasCookie = session.Jar.TryGetValue("__Host-anamnys-owner", out var sessionCookieValue);

        // Assert — the session cookie exists and holds an opaque reference, never the tokens.
        hasCookie.Should().BeTrue(
            $"a successful owner login must set the session cookie. Jar contents: {string.Join(", ", session.Jar.Keys)}");
        sessionCookieValue.Should().NotContain(
            "eyJ", "the cookie must hold an opaque ticket reference, never a JWT — SessionStore/ITicketStore " +
            "keep the actual access and refresh tokens server-side");
        sessionCookieValue!.Length.Should().BeLessThan(
            500, "a few hundred bytes at most: the cookie is a ticket-store key, not the serialized ticket itself");

        // Positive control: without it, a 401 from either replay below proves nothing.
        using var adminProbeResponse = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/admin/probe"), TestContext.Current.CancellationToken);
        adminProbeResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, "the owner session cookie must authenticate on the owners-only group");

        // Cookie-scheme isolation: the owners ticket replayed under the providers cookie
        // name must not authenticate on PHI (401, never 200 or 403). This rests on the
        // cookie handler's data protector including the scheme name in its purpose chain.
        using var replayedAsProvider = new HttpRequestMessage(HttpMethod.Get, "/api/phi/probe");
        replayedAsProvider.Headers.Add("Cookie", $"__Host-anamnys-provider={sessionCookieValue}");
        using var replayResponse = await session.Client.SendAsync(replayedAsProvider, TestContext.Current.CancellationToken);
        replayResponse.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "an owners-realm ticket replayed under the providers cookie name must not authenticate on PHI");

        // And the untouched owners cookie is not a PHI credential either: the owners realm
        // is absent from the /api/phi group on purpose, so this is 401, not 403.
        using var ownerOnPhiResponse = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/phi/probe"), TestContext.Current.CancellationToken);
        ownerOnPhiResponse.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized, "an owners cookie is not an authenticated principal on the PHI group at all");
    }
}
