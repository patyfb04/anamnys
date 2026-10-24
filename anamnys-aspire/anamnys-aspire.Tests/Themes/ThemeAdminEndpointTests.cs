using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Themes;

[Collection(SharedAppHostCollection.Name)]
public class ThemeAdminEndpointTests(SharedAppHostFixture fixture)
{
    private static readonly Uri AdminBaseAddress = new("http://localhost:5276/");
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private const string Base = "/api/admin/theme-vocabulary";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", Base },
        { "POST", $"{Base}/terms" },
        { "PUT", $"{Base}/terms/00000000-0000-0000-0000-000000000001/label" },
        { "POST", $"{Base}/terms/00000000-0000-0000-0000-000000000001/retire" },
        { "POST", $"{Base}/terms/00000000-0000-0000-0000-000000000001/split" },
        { "POST", $"{Base}/merge" },
    };

    private static HttpRequestMessage Request(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }
        return request;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_WithoutSession_Returns401(string method, string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = AdminBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/admin/", Ct);
        using var request = Request(method, path);

        // Act
        using var response = await client.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_WithProviderSession_Returns401(string method, string path)
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
        using var request = Request(method, path);

        // Act
        using var response = await session.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_AsSupport_ReturnsTheActiveVocabularyWithCounts()
    {
        // Arrange
        using var session = await StaffSessions.LoginAsync(fixture, "support", Ct);

        // Act
        using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, Base), Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var terms = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        terms.GetArrayLength().Should().BeGreaterThanOrEqualTo(40);
        var first = terms[0];
        foreach (var property in new[] { "id", "code", "label", "parentId", "status", "introducedOn", "successorIds", "activeTagCount" })
        {
            first.TryGetProperty(property, out _).Should().BeTrue($"'{property}' is part of the contract");
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Mutation_AsSupport_Returns403(string method, string path)
    {
        // Arrange
        if (method == "GET")
        {
            return;
        }
        using var session = await StaffSessions.LoginAsync(fixture, "support", Ct);
        using var request = Request(method, path);

        // Act
        using var response = await session.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("ops")]
    [InlineData("owner")]
    public async Task Mutations_AsOpsOrOwner_CreateRenameAndRetireATerm(string role)
    {
        // Arrange
        var code = $"teste_{role}_{Guid.NewGuid():N}"[..40];
        using var session = await StaffSessions.LoginAsync(fixture, role, Ct);
        try
        {
            // Act
            using var created = await session.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, $"{Base}/terms") { Content = JsonContent.Create(new { code, label = "Tema de teste" }) }, Ct);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
            using var renamed = await session.SendAsync(
                new HttpRequestMessage(HttpMethod.Put, $"{Base}/terms/{id}/label") { Content = JsonContent.Create(new { label = "Tema renomeado" }) }, Ct);
            using var duplicate = await session.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, $"{Base}/terms") { Content = JsonContent.Create(new { code, label = "Outro" }) }, Ct);
            using var retired = await session.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/terms/{id}/retire"), Ct);
            using var renamedAgain = await session.SendAsync(
                new HttpRequestMessage(HttpMethod.Put, $"{Base}/terms/{id}/label") { Content = JsonContent.Create(new { label = "Tarde demais" }) }, Ct);

            // Assert
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            renamed.StatusCode.Should().Be(HttpStatusCode.NoContent);
            duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await duplicate.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().Should().Be("code_taken");
            retired.StatusCode.Should().Be(HttpStatusCode.NoContent);
            renamedAgain.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally
        {
            await DeleteTermAsync(code);
        }
    }

    [Fact]
    public async Task Mutations_InvalidAndUnknown_Return400And404()
    {
        // Arrange
        using var session = await StaffSessions.LoginAsync(fixture, "ops", Ct);

        // Act
        using var invalid = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{Base}/terms") { Content = JsonContent.Create(new { code = "X", label = "" }) }, Ct);
        using var unknown = await session.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/terms/{Guid.NewGuid()}/retire"), Ct);
        using var badMerge = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{Base}/merge") { Content = JsonContent.Create(new { sourceIds = new[] { Guid.NewGuid() } }) }, Ct);

        // Assert
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        badMerge.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Retire_TopLevelTermWithActiveChildren_Returns409()
    {
        // Arrange
        using var session = await StaffSessions.LoginAsync(fixture, "ops", Ct);
        using var list = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, Base), Ct);
        var terms = await list.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var parentId = terms.EnumerateArray()
            .First(t => t.GetProperty("code").GetString() == "trabalho").GetProperty("id").GetGuid();

        // Act
        using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/terms/{parentId}/retire"), Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().Should().Be("has_active_children");
    }

    private async Task DeleteTermAsync(string code)
    {
        var connectionString = await fixture.GetConnectionStringAsync("anamnysdb", CancellationToken.None);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand("""
            DELETE FROM "ThemeTermChanges" WHERE "TermId" IN (SELECT "Id" FROM "ThemeTerms" WHERE "Code" = @code);
            DELETE FROM "ThemeTerms" WHERE "Code" = @code;
            """, connection);
        command.Parameters.AddWithValue("code", code);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
