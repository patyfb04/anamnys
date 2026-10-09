using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Notifications;

// The rules are covered in ProviderNotificationsTests; this covers the HTTP boundary.
[Collection(SharedAppHostCollection.Name)]
public class ProviderNotificationEndpointTests
{
    private const string BasePath = "/api/phi/providers/me";
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");

    [Theory]
    [InlineData("GET", "/notifications?page=1")]
    [InlineData("POST", "/notifications/00000000-0000-0000-0000-000000000001/read")]
    [InlineData("POST", "/notifications/read-all")]
    [InlineData("GET", "/booking-policy")]
    [InlineData("PUT", "/booking-policy")]
    public async Task Routes_WithoutSession_Return401(string method, string suffix)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(new HttpMethod(method), BasePath + suffix);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        // Act
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PutBookingPolicy_InvalidBody_Returns400WithFieldKeys()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026",
            TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, BasePath + "/booking-policy")
        {
            Content = JsonContent.Create(new { autoCancelMode = "x", autoCancelHours = 0 }),
        };

        // Act
        using var response = await session.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var errors = document.RootElement.GetProperty("errors");
        errors.TryGetProperty("autoCancelMode", out _).Should().BeTrue();
        errors.TryGetProperty("autoCancelHours", out _).Should().BeTrue();
    }
}
