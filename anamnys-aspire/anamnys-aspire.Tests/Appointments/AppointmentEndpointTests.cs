using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Appointments;

[Collection(SharedAppHostCollection.Name)]
public class AppointmentEndpointTests
{
    private const string BasePath = "/api/phi/providers/me/appointments";
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private const string AnyId = "00000000-0000-0000-0000-000000000001";

    [Theory]
    [InlineData("GET", "?from=2026-10-05T03:00:00Z&to=2026-10-12T03:00:00Z")]
    [InlineData("POST", "")]
    [InlineData("PUT", "/" + AnyId)]
    [InlineData("POST", "/" + AnyId + "/status")]
    public async Task Routes_WithoutSession_Return401(string method, string suffix)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(new HttpMethod(method), BasePath + suffix);
        if (method != "GET")
        {
            request.Content = JsonContent.Create(new { });
        }

        // Act
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_ForAWeek_Returns200WithItems()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await SendAsync(session, HttpMethod.Get,
            $"{BasePath}?from=2026-10-05T03:00:00Z&to=2026-10-12T03:00:00Z&status=scheduled&status=confirmed");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task List_WithoutRange_Returns400()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await SendAsync(session, HttpMethod.Get, BasePath);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await ReadJsonAsync(response)).GetProperty("errors");
        errors.TryGetProperty("from", out _).Should().BeTrue();
        errors.TryGetProperty("to", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Create_WithInvalidBody_Returns400WithFieldErrors()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await SendAsync(session, HttpMethod.Post, BasePath, new { durationMinutes = 1, modality = "x" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await ReadJsonAsync(response)).GetProperty("errors");
        foreach (var key in new[] { "patientId", "startsAt", "durationMinutes", "modality" })
        {
            errors.TryGetProperty(key, out _).Should().BeTrue(key);
        }
    }

    [Fact]
    public async Task UnknownIds_Return404()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var slot = new { startsAt = "2026-10-06T13:00:00Z", durationMinutes = 50, modality = "online" };

        // Act
        using var create = await SendAsync(session, HttpMethod.Post, BasePath,
            new { patientId = Guid.NewGuid(), slot.startsAt, slot.durationMinutes, slot.modality });
        using var update = await SendAsync(session, HttpMethod.Put, $"{BasePath}/{Guid.NewGuid()}", slot);
        using var status = await SendAsync(session, HttpMethod.Post, $"{BasePath}/{Guid.NewGuid()}/status", new { status = "confirmed" });

        // Assert
        new[] { create, update, status }.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
    }

    private static async Task<HttpResponseMessage> SendAsync(BrowserSession session, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await session.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026",
            TestContext.Current.CancellationToken);
}
