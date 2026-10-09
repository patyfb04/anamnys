using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Notifications;

// Anonymous calls only, and none that can write: an unknown token changes nothing.
[Collection(SharedAppHostCollection.Name)]
public class ConfirmationEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private const string UnknownToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpClient> ClientAsync()
    {
        var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        return client;
    }

    [Fact]
    public async Task Get_UnknownToken_ReturnsInvalid()
    {
        using var client = await ClientAsync();

        using var response = await client.GetAsync($"/api/public/appointment-confirmations/{UnknownToken}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().Should().Be("invalid");
    }

    [Fact]
    public async Task Post_UnknownToken_Returns410()
    {
        using var client = await ClientAsync();

        using var response = await client.PostAsync($"/api/public/appointment-confirmations/{UnknownToken}", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task PortalConfirm_WithoutSession_Returns401()
    {
        using var client = await ClientAsync();

        using var response = await client.PostAsync($"/api/phi/patients/me/appointments/{Guid.NewGuid()}/confirm", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
