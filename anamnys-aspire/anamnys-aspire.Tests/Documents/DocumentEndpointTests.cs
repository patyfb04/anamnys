using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Documents;

[Collection(SharedAppHostCollection.Name)]
public class DocumentEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("GET", "/api/phi/providers/me/document-kinds")]
    [InlineData("GET", "/api/phi/providers/me/document-settings")]
    [InlineData("PUT", "/api/phi/providers/me/document-settings")]
    [InlineData("GET", "/api/phi/providers/me/patients/00000000-0000-0000-0000-000000000001/documents")]
    [InlineData("POST", "/api/phi/providers/me/patients/00000000-0000-0000-0000-000000000001/documents")]
    [InlineData("POST", "/api/phi/providers/me/documents/search")]
    [InlineData("GET", "/api/phi/providers/me/documents/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/api/phi/providers/me/documents/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/phi/providers/me/documents/00000000-0000-0000-0000-000000000001/sign")]
    [InlineData("POST", "/api/phi/providers/me/documents/00000000-0000-0000-0000-000000000001/correct")]
    [InlineData("POST", "/api/phi/providers/me/documents/00000000-0000-0000-0000-000000000001/export")]
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

    [Fact]
    public async Task DocumentKinds_LoggedIn_ReturnsTheThreeAvailableKinds()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/phi/providers/me/document-kinds"), Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var kinds = body.RootElement.EnumerateArray().ToList();
        kinds.Select(k => k.GetProperty("kind").GetString()).Should().Equal("declaracao", "atestado", "relatorio");
        kinds.Should().OnlyContain(k => k.GetProperty("elements").GetArrayLength() > 0
            && !string.IsNullOrEmpty(k.GetProperty("rulesetVersion").GetString())
            && !string.IsNullOrEmpty(k.GetProperty("title").GetString()));
        kinds.Should().Contain(k => k.GetProperty("prohibitions").GetArrayLength() > 0);
    }

    [Fact]
    public async Task DocumentSettings_PutThenGet_RoundTripsAndRejectsOtherValues()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var original = await GetModeAsync(session);
        var other = original == "advisory" ? "blocking" : "advisory";

        try
        {
            // Act
            var put = await PutModeAsync(session, other);
            var saved = await GetModeAsync(session);
            var invalid = await PutModeAsync(session, "whatever");

            // Assert
            put.Should().Be(HttpStatusCode.OK);
            saved.Should().Be(other);
            invalid.Should().Be(HttpStatusCode.BadRequest);
            (await GetModeAsync(session)).Should().Be(other);
        }
        finally
        {
            await PutModeAsync(session, original);
        }
    }

    [Fact]
    public async Task DocumentSearch_InvalidStatus_Returns400AndValidFilterReturnsList()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var invalid = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/phi/providers/me/documents/search")
            {
                Content = JsonContent.Create(new { status = "open" }),
            }, Ct);
        using var valid = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/phi/providers/me/documents/search")
            {
                Content = JsonContent.Create(new { status = "signed", page = 1 }),
            }, Ct);

        // Assert
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        valid.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await valid.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
        body.RootElement.GetProperty("hasMore").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
    }

    [Fact]
    public async Task Documents_UnknownDocument_Returns404()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/phi/providers/me/documents/{Guid.NewGuid()}"), Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateDocument_UnavailableKind_Returns409KindNotAvailable()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/phi/providers/me/patients/{Guid.NewGuid()}/documents")
            {
                Content = JsonContent.Create(new { kind = "laudo" }),
            }, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("code").GetString().Should().Be("kind_not_available");
    }

    private static async Task<string> GetModeAsync(BrowserSession session)
    {
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/phi/providers/me/document-settings"), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("documentCheckMode").GetString()!;
    }

    private static async Task<HttpStatusCode> PutModeAsync(BrowserSession session, string mode)
    {
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, "/api/phi/providers/me/document-settings")
            {
                Content = JsonContent.Create(new { documentCheckMode = mode }),
            }, Ct);
        return response.StatusCode;
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
