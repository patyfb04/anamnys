using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Patients;

// The query's rules are covered in PatientSearchQueryTests; this covers the HTTP boundary.
[Collection(SharedAppHostCollection.Name)]
public class PatientEndpointTests
{
    private const string SearchPath = "/api/phi/providers/me/patients/search";
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");

    [Fact]
    public async Task Search_WithoutSession_Returns401()
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", TestContext.Current.CancellationToken);

        // Act
        using var response = await client.PostAsJsonAsync(SearchPath, new { }, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Search_AsProvider_ReturnsAPage()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, SearchPath) { Content = JsonContent.Create(new { pageSize = 5 }) },
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
        body.RootElement.GetProperty("totalCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        body.RootElement.GetProperty("page").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("pageSize").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Search_WithInvalidBody_Returns400WithFieldErrors()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, SearchPath) { Content = JsonContent.Create(new { sortBy = "age", pageSize = 500 }) },
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var errors = body.RootElement.GetProperty("errors");
        errors.TryGetProperty("sortBy", out _).Should().BeTrue();
        errors.TryGetProperty("pageSize", out _).Should().BeTrue();
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026",
            TestContext.Current.CancellationToken);
}
