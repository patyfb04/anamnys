using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Exports;

[Collection(SharedAppHostCollection.Name)]
public class ExportEndpointTests
{
    private const string BasePath = "/api/phi/providers/me/exports";
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("POST", "")]
    [InlineData("GET", "")]
    [InlineData("POST", "/00000000-0000-0000-0000-000000000001/download")]
    public async Task Route_WithoutSession_Returns401(string method, string suffix)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), BasePath + suffix);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { });
        }

        // Act
        using var response = await client.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Download_UnknownExport_Returns404()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/{Guid.NewGuid()}/download") { Content = JsonContent.Create(new { }) }, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Export_RequestThenWaitThenDownload_ProducesAZipTheLinkServes()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        await WaitUntilNoneInProgressAsync(session);

        // Act
        using var requested = await session.SendAsync(new HttpRequestMessage(HttpMethod.Post, BasePath) { Content = JsonContent.Create(new { }) }, Ct);
        using var again = await session.SendAsync(new HttpRequestMessage(HttpMethod.Post, BasePath) { Content = JsonContent.Create(new { }) }, Ct);

        // Assert
        requested.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var created = JsonDocument.Parse(await requested.Content.ReadAsStringAsync(Ct));
        var id = created.RootElement.GetProperty("id").GetGuid();
        again.StatusCode.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.Accepted);
        if (again.StatusCode == HttpStatusCode.Conflict)
        {
            using var conflict = JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct));
            conflict.RootElement.GetProperty("code").GetString().Should().Be("export_in_progress");
        }

        var latest = await WaitUntilFinishedAsync(session);
        latest.GetProperty("status").GetString().Should().Be("done");
        latest.GetProperty("sizeBytes").GetInt64().Should().BeGreaterThan(0);
        latest.GetProperty("expiresAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddDays(6));

        using var download = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/{latest.GetProperty("id").GetGuid()}/download") { Content = JsonContent.Create(new { }) }, Ct);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        using var link = JsonDocument.Parse(await download.Content.ReadAsStringAsync(Ct));
        var url = link.RootElement.GetProperty("url").GetString()!;
        using var plain = new HttpClient();
        using var file = await plain.GetAsync(url, Ct);
        file.StatusCode.Should().Be(HttpStatusCode.OK);
        file.Content.Headers.ContentDisposition!.FileName.Should().Contain("anamnys-exportacao-").And.Contain(".zip");
        (await file.Content.ReadAsByteArrayAsync(Ct))[..2].Should().Equal((byte)'P', (byte)'K');
        id.Should().NotBeEmpty();
    }

    private static async Task WaitUntilNoneInProgressAsync(BrowserSession session)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, BasePath), Ct);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return;
            }
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            if (body.RootElement.GetProperty("status").GetString() is not ("queued" or "running"))
            {
                return;
            }
            await Task.Delay(500, Ct);
        }
    }

    private static async Task<JsonElement> WaitUntilFinishedAsync(BrowserSession session)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (true)
        {
            using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, BasePath), Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            if (body.RootElement.GetProperty("status").GetString() is not ("queued" or "running") || DateTimeOffset.UtcNow > deadline)
            {
                return body.RootElement.Clone();
            }
            body.Dispose();
            await Task.Delay(500, Ct);
        }
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
