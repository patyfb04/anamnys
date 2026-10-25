using System.Net;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Excerpts;

[Collection(SharedAppHostCollection.Name)]
public class ExcerptEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Passage_WithoutSession_Returns401()
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        // Act
        using var response = await client.GetAsync($"/api/phi/providers/me/excerpts/{Guid.NewGuid()}", Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Passage_UnknownId_Returns404()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/phi/providers/me/excerpts/{Guid.NewGuid()}"), Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
