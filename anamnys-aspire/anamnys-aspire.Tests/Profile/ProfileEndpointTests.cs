using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Profile;

// Only the seeded dev.provider exists in Keycloak (there is no seeded patient), so the
// patient routes are covered for their auth boundary here and for their rules in
// ProfileValidationTests.
[Collection(SharedAppHostCollection.Name)]
public class ProfileEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");

    [Theory]
    [InlineData("/api/phi/providers/me/profile")]
    [InlineData("/api/phi/patients/me/profile")]
    public async Task Profile_WithoutSession_Returns401(string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", TestContext.Current.CancellationToken);

        // Act
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PatientProfile_WithOnlyAProviderSession_Returns401()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/phi/patients/me/profile"), TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProviderProfile_PutWithBlankName_Returns400WithNameError()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, "/api/phi/providers/me/profile")
            {
                Content = JsonContent.Create(new { name = " ", crpNumber = (string?)null, crpRegion = (string?)null }),
            },
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("errors").TryGetProperty("name", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ProviderProfile_PutThenGet_ReturnsTheSavedValues()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var original = await GetProviderProfileAsync(session);
        var newName = $"Dev Provider {Guid.NewGuid():N}"[..24];

        try
        {
            // Act
            using var put = await session.SendAsync(
                new HttpRequestMessage(HttpMethod.Put, "/api/phi/providers/me/profile")
                {
                    Content = JsonContent.Create(new { name = newName, crpNumber = "06/99999", crpRegion = "SP" }),
                },
                TestContext.Current.CancellationToken);
            var saved = await GetProviderProfileAsync(session);

            // Assert
            put.StatusCode.Should().Be(HttpStatusCode.NoContent);
            saved.GetProperty("name").GetString().Should().Be(newName);
            saved.GetProperty("crpNumber").GetString().Should().Be("06/99999");
            saved.GetProperty("crpRegion").GetString().Should().Be("SP");
            saved.GetProperty("email").GetString().Should().Be(original.GetProperty("email").GetString());
        }
        finally
        {
            // Leave dev.provider as it was for anyone using the dev database.
            using var restore = await session.SendAsync(
                new HttpRequestMessage(HttpMethod.Put, "/api/phi/providers/me/profile")
                {
                    Content = JsonContent.Create(new
                    {
                        name = original.GetProperty("name").GetString(),
                        crpNumber = original.GetProperty("crpNumber").GetString(),
                        crpRegion = original.GetProperty("crpRegion").GetString(),
                    }),
                },
                TestContext.Current.CancellationToken);
        }
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026",
            TestContext.Current.CancellationToken);

    private static async Task<JsonElement> GetProviderProfileAsync(BrowserSession session)
    {
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/phi/providers/me/profile"), TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }
}
