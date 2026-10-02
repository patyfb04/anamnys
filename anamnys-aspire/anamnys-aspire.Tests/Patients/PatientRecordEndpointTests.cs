using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Patients;

// The rules are covered in PatientRecordsTests; this covers the HTTP boundary as the seeded
// dev.provider. Every patient created here is deleted again so the dev database stays clean.
[Collection(SharedAppHostCollection.Name)]
public class PatientRecordEndpointTests
{
    private const string BasePath = "/api/phi/providers/me/patients";
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");

    [Theory]
    [InlineData("GET", "/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "")]
    [InlineData("DELETE", "/00000000-0000-0000-0000-000000000001")]
    public async Task Routes_WithoutSession_Return401(string method, string suffix)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(new HttpMethod(method), BasePath + suffix);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { });
        }

        // Act
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateGetDelete_RoundTrip()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var created = await SendAsync(session, HttpMethod.Post, BasePath, new
        {
            firstName = "Teste",
            lastName = "Endpoint",
            contactEmail = "teste.endpoint@example.com",
            dateOfBirth = "1990-05-12",
            diagnoses = new[] { new { description = "Teste", icdCode = "F41.1" } },
        });
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetString();
        using var detail = await SendAsync(session, HttpMethod.Get, $"{BasePath}/{id}");
        var detailBody = await ReadJsonAsync(detail);
        using var deleted = await SendAsync(session, HttpMethod.Delete, $"{BasePath}/{id}");
        using var afterDelete = await SendAsync(session, HttpMethod.Get, $"{BasePath}/{id}");

        // Assert
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        detailBody.GetProperty("lastName").GetString().Should().Be("Endpoint");
        detailBody.GetProperty("diagnoses").GetArrayLength().Should().Be(1);
        detailBody.GetProperty("canDelete").GetBoolean().Should().BeTrue();
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        afterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_WithInvalidBody_Returns400WithFieldErrors()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await SendAsync(session, HttpMethod.Post, BasePath, new
        {
            firstName = "",
            lastName = "X",
            contactEmail = "nao-e-email",
            medications = new[] { new { drug = "" } },
        });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await ReadJsonAsync(response)).GetProperty("errors");
        errors.TryGetProperty("firstName", out _).Should().BeTrue();
        errors.TryGetProperty("dateOfBirth", out _).Should().BeTrue();
        errors.TryGetProperty("medications[0].drug", out _).Should().BeTrue();
        errors.TryGetProperty("contactEmail", out _).Should().BeTrue();
    }

    [Fact]
    public async Task UnknownPatient_Returns404OnEveryRoute()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var path = $"{BasePath}/{Guid.NewGuid()}";

        // Act
        using var get = await SendAsync(session, HttpMethod.Get, path);
        using var put = await SendAsync(session, HttpMethod.Put, path, new { firstName = "A", lastName = "B", contactEmail = "a@b.co", dateOfBirth = "1990-01-01" });
        using var archive = await SendAsync(session, HttpMethod.Post, $"{path}/archive");
        using var addDiagnosis = await SendAsync(session, HttpMethod.Post, $"{path}/diagnoses", new { description = "x" });
        using var removeObjective = await SendAsync(session, HttpMethod.Delete, $"{path}/objectives/{Guid.NewGuid()}");

        // Assert
        new[] { get, put, archive, addDiagnosis, removeObjective }
            .Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
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
