using System.Net;
using System.Net.Http.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Themes;

[Collection(SharedAppHostCollection.Name)]
public class ThemeEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("GET", "/api/phi/providers/me/theme-vocabulary")]
    [InlineData("GET", "/api/phi/providers/me/notes/00000000-0000-0000-0000-000000000001/themes")]
    [InlineData("POST", "/api/phi/providers/me/notes/00000000-0000-0000-0000-000000000001/themes")]
    [InlineData("DELETE", "/api/phi/providers/me/notes/00000000-0000-0000-0000-000000000001/themes/00000000-0000-0000-0000-000000000002")]
    [InlineData("POST", "/api/phi/providers/me/notes/00000000-0000-0000-0000-000000000001/themes/suggest")]
    public async Task Route_WithoutSession_Returns401(string method, string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { });
        }

        // Act
        using var response = await client.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Themes_UnknownNote_Returns404(string method)
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var path = $"/api/phi/providers/me/notes/{Guid.NewGuid()}/themes";
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { termId = Guid.NewGuid() });
        }

        // Act
        using var response = await session.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Suggest_UnknownNote_Returns404()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/phi/providers/me/notes/{Guid.NewGuid()}/themes/suggest"), Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Themes_PostWithoutTermId_Returns400()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/phi/providers/me/notes/{Guid.NewGuid()}/themes")
            {
                Content = JsonContent.Create(new { }),
            }, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
