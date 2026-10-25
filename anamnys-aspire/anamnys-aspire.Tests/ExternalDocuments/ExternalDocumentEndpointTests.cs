using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.ExternalDocuments;

// Through the provider dev server against the real server (scanner = fake: the fixture has no ClamAV).
[Collection(SharedAppHostCollection.Name)]
public class ExternalDocumentEndpointTests
{
    private const string PatientsPath = "/api/phi/providers/me/patients";
    private const string DocumentsPath = "/api/phi/providers/me/external-documents";
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static readonly byte[] Pdf = [.. "%PDF-1.7\nendpoint test"u8];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("POST", "/api/phi/providers/me/patients/00000000-0000-0000-0000-000000000001/external-documents")]
    [InlineData("GET", "/api/phi/providers/me/patients/00000000-0000-0000-0000-000000000001/external-documents")]
    [InlineData("POST", DocumentsPath + "/search")]
    [InlineData("POST", DocumentsPath + "/00000000-0000-0000-0000-000000000001/open")]
    [InlineData("DELETE", DocumentsPath + "/00000000-0000-0000-0000-000000000001")]
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

    [Fact]
    public async Task Upload_ThenScan_ThenOpenAndDelete_WorksEndToEnd_AndAnEicarFileIsBlocked()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var patient = await CreatePatientAsync(session);

        // Act
        using var uploaded = await UploadAsync(session, patient, "Laudo.pdf", Pdf, "laudo");
        using var eicar = await UploadAsync(session, patient, "virus.pdf",
            [.. "%PDF-1.4\nX5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"u8], "exame");

        // Assert
        uploaded.StatusCode.Should().Be(HttpStatusCode.Created);
        eicar.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(uploaded)).GetProperty("id").GetGuid();
        var blockedId = (await Json(eicar)).GetProperty("id").GetGuid();
        (await Json(uploaded)).GetProperty("scanStatus").GetString().Should().Be("pending");

        await WaitUntilScannedAsync(session, patient, id);
        await WaitUntilScannedAsync(session, patient, blockedId);
        using var list = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{PatientsPath}/{patient}/external-documents"), Ct);
        var items = (await Json(list)).EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());
        items[id].GetProperty("scanStatus").GetString().Should().Be("clean");
        items[id].GetProperty("fileName").GetString().Should().Be("Laudo.pdf");
        items[blockedId].GetProperty("scanStatus").GetString().Should().Be("infected");

        using var blockedOpen = await session.SendAsync(Post($"{DocumentsPath}/{blockedId}/open"), Ct);
        blockedOpen.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(blockedOpen)).GetProperty("code").GetString().Should().Be("not_available");

        using var open = await session.SendAsync(Post($"{DocumentsPath}/{id}/open"), Ct);
        open.StatusCode.Should().Be(HttpStatusCode.OK);
        var url = (await Json(open)).GetProperty("url").GetString()!;
        url.Should().NotContain("Laudo");
        using var plain = new HttpClient();
        using var file = await plain.GetAsync(url, Ct);
        file.StatusCode.Should().Be(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        file.Content.Headers.ContentDisposition?.DispositionType.Should().Be("inline");
        (await file.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(Pdf);

        using var search = await session.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{DocumentsPath}/search")
        {
            Content = JsonContent.Create(new { kind = "laudo", patientId = patient }),
        }, Ct);
        (await Json(search)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(id);

        using var deleted = await session.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{DocumentsPath}/{id}"), Ct);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var gone = await session.SendAsync(Post($"{DocumentsPath}/{id}/open"), Ct);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("a.txt", "just text", "unsupported_type")]
    [InlineData("a.png", "%PDF-1.4", "type_mismatch")]
    public async Task Upload_BadFile_Returns400WithACode(string name, string content, string code)
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var patient = await CreatePatientAsync(session);

        // Act
        using var response = await UploadAsync(session, patient, name, System.Text.Encoding.ASCII.GetBytes(content), "laudo");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be(code);
    }

    [Fact]
    public async Task Upload_EmptyFileInvalidKindUnknownPatientAndArchivedPatient_AreRefused()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var patient = await CreatePatientAsync(session);
        var archived = await CreatePatientAsync(session);
        using (var archive = await session.SendAsync(Post($"{PatientsPath}/{archived}/archive"), Ct))
        {
            archive.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        // Act
        using var empty = await UploadAsync(session, patient, "a.pdf", [], "laudo");
        using var badKind = await UploadAsync(session, patient, "a.pdf", Pdf, "whatever");
        using var unknown = await UploadAsync(session, Guid.NewGuid(), "a.pdf", Pdf, "laudo");
        using var inArchived = await UploadAsync(session, archived, "a.pdf", Pdf, "laudo");

        // Assert
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(empty)).GetProperty("code").GetString().Should().Be("empty");
        badKind.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        inArchived.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(inArchived)).GetProperty("code").GetString().Should().Be("archived");
    }

    [Fact]
    public async Task Upload_FileOverTwentyMegabytes_Returns400TooLarge()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var patient = await CreatePatientAsync(session);
        var big = new byte[20 * 1024 * 1024 + 1];
        "%PDF-"u8.CopyTo(big);

        // Act
        using var response = await UploadAsync(session, patient, "big.pdf", big, "laudo");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(response)).GetProperty("code").GetString().Should().Be("too_large");
    }

    [Fact]
    public async Task Open_UnknownDocument_Returns404()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(Post($"{DocumentsPath}/{Guid.NewGuid()}/open"), Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static HttpRequestMessage Post(string path) => new(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) };

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<Guid> CreatePatientAsync(BrowserSession session)
    {
        using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Post, PatientsPath)
        {
            Content = JsonContent.Create(new { firstName = "Teste", lastName = "Documentos", contactEmail = "documentos@test.local", dateOfBirth = "1990-01-01" }),
        }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> UploadAsync(BrowserSession session, Guid patient, string fileName, byte[] content, string kind)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var form = new MultipartFormDataContent { { new StringContent(kind), "kind" }, { file, "file", fileName } };
        return session.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{PatientsPath}/{patient}/external-documents") { Content = form }, Ct);
    }

    private static async Task WaitUntilScannedAsync(BrowserSession session, Guid patient, Guid id)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var list = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{PatientsPath}/{patient}/external-documents"), Ct);
            var status = (await Json(list)).EnumerateArray().First(i => i.GetProperty("id").GetGuid() == id).GetProperty("scanStatus").GetString();
            if (status != "pending")
            {
                return;
            }
            await Task.Delay(300, Ct);
        }
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
