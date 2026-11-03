using System.Net;
using System.Net.Http.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Sharing;

[Collection(SharedAppHostCollection.Name)]
public class ShareEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("GET", "/api/phi/providers/me/documents/00000000-0000-0000-0000-000000000001/shares")]
    [InlineData("POST", "/api/phi/providers/me/documents/00000000-0000-0000-0000-000000000001/disclosure-authorizations")]
    [InlineData("POST", "/api/phi/providers/me/documents/00000000-0000-0000-0000-000000000001/share-links")]
    [InlineData("POST", "/api/phi/providers/me/share-links/00000000-0000-0000-0000-000000000001/revoke")]
    public async Task Route_WithoutSession_Returns401(string method, string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET")
        {
            request.Content = JsonContent.Create(new { });
        }

        // Act
        using var response = await client.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
