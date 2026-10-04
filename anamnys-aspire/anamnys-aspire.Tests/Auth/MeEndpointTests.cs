using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// /api/auth/me without a realm answers for whichever session it finds first (provider,
// then owner, then patient), so an app that trusted it accepted another realm's user: the
// provider app opened for a patient-only session, the patient portal showed a provider. Each
// app now asks for its own realm.
[Collection(SharedAppHostCollection.Name)]
public class MeEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Me_ForTheSessionsOwnRealm_ReturnsTheUser()
    {
        using var session = await LoginAsProviderAsync();

        using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/auth/me?realm=provider"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("realm").GetString().Should().Be("anamnys-providers");
    }

    [Theory]
    [InlineData("patient")]
    [InlineData("owner")]
    public async Task Me_ForAnotherRealm_Returns401EvenWithAProviderSession(string realm)
    {
        using var session = await LoginAsProviderAsync();

        using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/auth/me?realm={realm}"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_WithAnUnknownRealm_Returns400()
    {
        using var session = await LoginAsProviderAsync();

        using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/auth/me?realm=admin"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Me_WithoutARealm_KeepsAnsweringForAnySession()
    {
        // The public site only needs to know whether someone is signed in.
        using var session = await LoginAsProviderAsync();

        using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/auth/me"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
