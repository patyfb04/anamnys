using System.Net;
using System.Net.Http.Json;
using Anamnys.Server.Scales;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Scales;

[Collection(SharedAppHostCollection.Name)]
public class ScaleEndpointTests(SharedAppHostFixture fixture)
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListInstruments_WithoutSession_Returns401()
    {
        // Arrange
        _ = fixture;
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        // Act
        using var response = await client.GetAsync("/api/phi/providers/me/instruments", Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Record_WithoutSession_Returns401()
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        // Act
        using var response = await client.PostAsJsonAsync(
            $"/api/phi/providers/me/patients/{Guid.NewGuid()}/scales", new { instrumentId = Guid.NewGuid(), score = 3 }, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListInstruments_WithSession_Returns15ItemsWithPhq9Available()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);

        // Act
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/phi/providers/me/instruments");
        using var response = await session.SendAsync(request, Ct);
        var items = await response.Content.ReadFromJsonAsync<List<InstrumentItem>>(Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        items.Should().HaveCount(15);
        items!.Single(i => i.Code == "PHQ-9").Regime.Should().Be(ScaleRegime.Available);
    }
}
