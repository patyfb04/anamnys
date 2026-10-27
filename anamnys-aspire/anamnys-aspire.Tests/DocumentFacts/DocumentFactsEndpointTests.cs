using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Server.DocumentFacts;
using Anamnys.Tests.Auth;
using Anamnys.Tests.DocumentReading;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.DocumentFacts;

// Through the provider dev server against the real server: fake scanner, real reader, and no model
// (DocumentFacts:Extractor = none, so the server's extraction saves no facts). The facts under test are
// written with FactService on the read pages, as the extractor would.
[Collection(SharedAppHostCollection.Name)]
public class DocumentFactsEndpointTests(SharedAppHostFixture fixture)
{
    private const string PatientsPath = "/api/phi/providers/me/patients";
    private const string DocumentsPath = "/api/phi/providers/me/external-documents";
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("GET", DocumentsPath + "/00000000-0000-0000-0000-000000000001/facts")]
    [InlineData("POST", DocumentsPath + "/00000000-0000-0000-0000-000000000001/facts/00000000-0000-0000-0000-000000000002/confirm")]
    [InlineData("POST", DocumentsPath + "/00000000-0000-0000-0000-000000000001/facts/00000000-0000-0000-0000-000000000002/correct")]
    [InlineData("POST", DocumentsPath + "/00000000-0000-0000-0000-000000000001/facts/00000000-0000-0000-0000-000000000002/discard")]
    [InlineData("POST", DocumentsPath + "/00000000-0000-0000-0000-000000000001/extract")]
    [InlineData("GET", PatientsPath + "/00000000-0000-0000-0000-000000000001/declared-facts")]
    public async Task Route_WithoutSession_Returns401(string method, string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { value = "x" });
        }

        // Act
        using var response = await client.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnknownDocumentOrPatient_Returns404()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var facts = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{DocumentsPath}/{Guid.NewGuid()}/facts"), Ct);
        using var confirm = await session.SendAsync(Post($"{DocumentsPath}/{Guid.NewGuid()}/facts/{Guid.NewGuid()}/confirm"), Ct);
        using var extract = await session.SendAsync(Post($"{DocumentsPath}/{Guid.NewGuid()}/extract"), Ct);
        using var declared = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{PatientsPath}/{Guid.NewGuid()}/declared-facts"), Ct);

        // Assert
        facts.StatusCode.Should().Be(HttpStatusCode.NotFound);
        confirm.StatusCode.Should().Be(HttpStatusCode.NotFound);
        extract.StatusCode.Should().Be(HttpStatusCode.NotFound);
        declared.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UploadedDocument_IsRead_ThenItsFactsAreReviewed_DeclaredAndKeptOnReextraction()
    {
        // Arrange: upload a sample, wait for the scan, the read and the (empty) extraction.
        using var session = await LoginAsProviderAsync();
        var patient = await CreatePatientAsync(session);
        using var uploaded = await UploadAsync(session, patient, "laudo.pdf", TestPdfs.Sample("fernanda-barbosa-laudo-psiquiatrico.pdf"));
        uploaded.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await Json(uploaded)).GetProperty("id").GetGuid();
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await WaitForExtractionsAsync(seed, id, 1);
        await using var db = seed.CreateDbContext();
        var providerId = await db.ExternalDocuments.Where(d => d.Id == id).Select(d => d.ProviderId).SingleAsync(Ct);
        (await FactService.SaveExtractedAsync(db, providerId, id,
        [
            new("icd", "F43.1", 1, "CID-10 F43.1"),
            new("issue_date", "14 de setembro de 2026", 1, "14 de setembro de 2026"),
            new("registration", "CRM-SP 000000", 1, "CRM-SP 000000"),
        ], DateTimeOffset.UtcNow, Ct)).Should().Be(3);

        // Act / Assert: the list shows the read and the facts waiting.
        using (var list = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{PatientsPath}/{patient}/external-documents"), Ct))
        {
            var item = (await Json(list)).EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);
            item.GetProperty("readStatus").GetString().Should().Be("done");
            item.GetProperty("pendingFacts").GetInt32().Should().Be(3);
            item.GetProperty("extracting").GetBoolean().Should().BeFalse();
        }
        var facts = await FactsAsync(session, id);
        facts.GetProperty("readStatus").GetString().Should().Be("done");
        var byKind = facts.GetProperty("facts").EnumerateArray().ToDictionary(f => f.GetProperty("kind").GetString()!);
        byKind.Keys.Should().BeEquivalentTo("icd", "issue_date", "registration");
        var icd = byKind["icd"].GetProperty("id").GetGuid();
        var date = byKind["issue_date"].GetProperty("id").GetGuid();
        var registration = byKind["registration"].GetProperty("id").GetGuid();
        var icdRef = byKind["icd"].GetProperty("excerptRefId").GetGuid();

        using (var confirm = await session.SendAsync(Post($"{DocumentsPath}/{id}/facts/{icd}/confirm"), Ct))
        {
            confirm.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        using (var invalid = await session.SendAsync(Post($"{DocumentsPath}/{id}/facts/{registration}/correct", new { value = "CRM" }), Ct))
        {
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        using (var correct = await session.SendAsync(Post($"{DocumentsPath}/{id}/facts/{registration}/correct", new { value = "CRM-SP 123456" }), Ct))
        {
            correct.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        using (var discard = await session.SendAsync(Post($"{DocumentsPath}/{id}/facts/{date}/discard"), Ct))
        {
            discard.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        using (var confirmDiscarded = await session.SendAsync(Post($"{DocumentsPath}/{id}/facts/{date}/confirm"), Ct))
        {
            confirmDiscarded.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Json(confirmDiscarded)).GetProperty("code").GetString().Should().Be("not_pending");
        }

        using (var declared = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{PatientsPath}/{patient}/declared-facts"), Ct))
        {
            declared.StatusCode.Should().Be(HttpStatusCode.OK);
            var items = (await Json(declared)).EnumerateArray().ToList();
            items.Select(i => (i.GetProperty("kind").GetString(), i.GetProperty("value").GetString(), i.GetProperty("corrected").GetBoolean()))
                .Should().BeEquivalentTo(new[] { ("registration", "CRM-SP 123456", true), ("icd", "F43.1", false) });
            items.Should().OnlyContain(i => i.GetProperty("documentId").GetGuid() == id);
        }

        using (var passage = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/phi/providers/me/excerpts/{icdRef}"), Ct))
        {
            passage.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await Json(passage);
            body.GetProperty("sourceType").GetString().Should().Be("document");
            body.GetProperty("kind").GetString().Should().Be("exact");
            body.GetProperty("documentId").GetGuid().Should().Be(id);
            body.GetProperty("pageNumber").GetInt32().Should().Be(1);
            var text = body.GetProperty("pageText").GetString()!;
            text[body.GetProperty("start").GetInt32()..body.GetProperty("end").GetInt32()].Should().Be("CID-10 F43.1");
        }

        // "Ler de novo": the server's extraction finds nothing; the reviewed facts stay.
        using (var extract = await session.SendAsync(Post($"{DocumentsPath}/{id}/extract"), Ct))
        {
            extract.StatusCode.Should().Be(HttpStatusCode.Accepted);
            (await Json(extract)).GetProperty("readStatus").GetString().Should().Be("done");
        }
        await WaitForExtractionsAsync(seed, id, 2);
        var after = (await FactsAsync(session, id)).GetProperty("facts").EnumerateArray()
            .Select(f => (f.GetProperty("kind").GetString(), f.GetProperty("status").GetString())).ToList();
        after.Should().BeEquivalentTo(new[] { ("registration", "corrected"), ("icd", "confirmed") });
    }

    private static async Task WaitForExtractionsAsync(PatientSearchSeed seed, Guid documentId, int count)
    {
        var prefix = $"{documentId} ";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        await using var db = seed.CreateDbContext();
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await db.AuditEntries.CountAsync(a => a.Action == "facts_extracted" && a.NewValue!.StartsWith(prefix), Ct) >= count)
            {
                return;
            }
            await Task.Delay(300, Ct);
        }
        throw new TimeoutException("The document was not read and extracted in time.");
    }

    private static async Task<JsonElement> FactsAsync(BrowserSession session, Guid id)
    {
        using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{DocumentsPath}/{id}/facts"), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await Json(response);
    }

    private static HttpRequestMessage Post(string path, object? body = null) =>
        new(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<Guid> CreatePatientAsync(BrowserSession session)
    {
        using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Post, PatientsPath)
        {
            Content = JsonContent.Create(new { firstName = "Teste", lastName = "Fatos", contactEmail = "fatos@test.local", dateOfBirth = "1990-01-01" }),
        }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> UploadAsync(BrowserSession session, Guid patient, string fileName, byte[] content)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        var form = new MultipartFormDataContent { { new StringContent("laudo"), "kind" }, { file, "file", fileName } };
        return session.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{PatientsPath}/{patient}/external-documents") { Content = form }, Ct);
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
