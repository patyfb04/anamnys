using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Server.Dossiers;
using Anamnys.Server.Storage;
using Anamnys.Tests.Auth;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using QuestPDF.Infrastructure;
using static Anamnys.Tests.Integrity.IntegrityTestData;

namespace Anamnys.Tests.Dossiers;

// design/specs/2026-11-04-patient-dossier-design.md §3, §7. The routes answer 401 without a session through the running app;
// the handlers' answers (codes, shapes, 404, no-store, file names) are checked on a scratch database, since the dev database
// the signed-in tests reach may not have the F-44 migration applied.
[Collection(SharedAppHostCollection.Name)]
public class DossierEndpointTests(SharedAppHostFixture fixture)
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DossierEndpointTests() => QuestPDF.Settings.License = LicenseType.Community;

    [Theory]
    [InlineData("POST", "/api/phi/patients/00000000-0000-0000-0000-000000000001/dossiers")]
    [InlineData("GET", "/api/phi/patients/00000000-0000-0000-0000-000000000001/dossiers?page=1")]
    [InlineData("POST", "/api/phi/dossiers/00000000-0000-0000-0000-000000000001/download")]
    [InlineData("GET", "/api/phi/dossiers/00000000-0000-0000-0000-000000000001/receipt.pdf")]
    [InlineData("GET", "/api/phi/dossiers/00000000-0000-0000-0000-000000000001/timestamp.tsr")]
    public async Task Route_WithoutSession_Returns401(string method, string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { reason = "crp" });
        }

        // Act
        using var response = await client.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record Scene(RetentionScratch Db, Guid Provider, Guid Patient, Guid OtherProvider, Guid Stranger) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Scene> SceneAsync()
    {
        var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Benedita", Ct);
        var other = await s.AddProviderAsync(Ct);
        var stranger = await s.AddPatientAsync(other, "Estranha", Ct);
        return new Scene(s, provider, patient, other, stranger);
    }

    private static async Task<Answer> RequestAsync(Scene s, DossierQueue queue, Guid provider, Guid patient, DossierRequestBody? body)
    {
        await using var db = s.Db.CreateDbContext();
        return await ExecuteAsync(http => DossierEndpoints.RequestAsync(http, db, queue, provider, patient, body, Now, Ct));
    }

    [Fact]
    public async Task RequestAsync_Answers202ThenConflictAndQueuesOneJob()
    {
        // Arrange
        await using var s = await SceneAsync();
        var queue = new DossierQueue();

        // Act
        var accepted = await RequestAsync(s, queue, s.Provider, s.Patient, new DossierRequestBody("crp", null, null, null));
        var again = await RequestAsync(s, queue, s.Provider, s.Patient, new DossierRequestBody("court", null, null, null));

        // Assert
        accepted.Status.Should().Be(StatusCodes.Status202Accepted);
        accepted.CacheControl.Should().Be("no-store");
        using var created = JsonDocument.Parse(accepted.Body);
        var id = created.RootElement.GetProperty("id").GetGuid();
        created.RootElement.GetProperty("status").GetString().Should().Be("queued");
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job.Should().Be(new DossierJob(id, s.Provider));
        queue.Reader.TryRead(out _).Should().BeFalse();
        again.Status.Should().Be(StatusCodes.Status409Conflict);
        again.CacheControl.Should().Be("no-store");
        Code(again).Should().Be("dossier_in_progress");
    }

    [Theory]
    [InlineData(null, null, null, null, "invalid_reason")]
    [InlineData("judicial", null, null, null, "invalid_reason")]
    [InlineData("other", null, null, null, "reason_note_required")]
    [InlineData("other", "", null, null, "reason_note_required")]
    [InlineData("crp", null, "2026-10-31", "2026-10-01", "invalid_period")]
    public async Task RequestAsync_BadInput_Answers400WithTheCode(string? reason, string? note, string? from, string? to, string code)
    {
        // Arrange
        await using var s = await SceneAsync();
        var queue = new DossierQueue();
        var body = new DossierRequestBody(
            reason, note, from is null ? null : DateOnly.ParseExact(from, "yyyy-MM-dd"), to is null ? null : DateOnly.ParseExact(to, "yyyy-MM-dd"));

        // Act
        var answer = await RequestAsync(s, queue, s.Provider, s.Patient, body);
        var noBody = await RequestAsync(s, queue, s.Provider, s.Patient, null);

        // Assert
        answer.Status.Should().Be(StatusCodes.Status400BadRequest);
        answer.CacheControl.Should().Be("no-store");
        Code(answer).Should().Be(code);
        Code(noBody).Should().Be("invalid_reason");
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handlers_AnotherProvidersPatientOrDossier_Answer404()
    {
        // Arrange
        await using var s = await SceneAsync();
        var queue = new DossierQueue();
        var theirs = await RequestAsync(s, queue, s.OtherProvider, s.Stranger, new DossierRequestBody("crp", null, null, null));
        using var created = JsonDocument.Parse(theirs.Body);
        var theirId = created.RootElement.GetProperty("id").GetGuid();
        await using var db = s.Db.CreateDbContext();
        var store = new FakeNoteFileStore();

        // Act
        var request = await RequestAsync(s, queue, s.Provider, s.Stranger, new DossierRequestBody("crp", null, null, null));
        var list = await ExecuteAsync(http => DossierEndpoints.ListAsync(http, db, s.Provider, s.Stranger, 1, Now, Ct));
        var download = await ExecuteAsync(http => DossierEndpoints.DownloadAsync(http, db, store, s.Provider, theirId, Now, Ct));
        var receipt = await ExecuteAsync(http => DossierEndpoints.ReceiptAsync(http, db, s.Provider, theirId, Now, Ct));
        var token = await ExecuteAsync(http => DossierEndpoints.TokenAsync(http, db, s.Provider, theirId, Now, Ct));

        // Assert
        foreach (var answer in new[] { request, list, download, receipt, token })
        {
            answer.Status.Should().Be(StatusCodes.Status404NotFound);
            answer.CacheControl.Should().Be("no-store");
        }
    }

    [Fact]
    public async Task Handlers_GeneratedDossier_ListDownloadAndReceiptAnswerTheirShapes()
    {
        // Arrange
        await using var s = await SceneAsync();
        var queue = new DossierQueue();
        await RequestAsync(s, queue, s.Provider, s.Patient, new DossierRequestBody("other", "Pedido da escola", new DateOnly(2026, 10, 1), null));
        queue.Reader.TryRead(out var job).Should().BeTrue();
        var store = new FakeNoteFileStore();
        var services = new ServiceCollection();
        services.AddScoped(_ => s.Db.CreateDbContext());
        services.AddSingleton<INoteFileStore>(store);
        services.AddSingleton<Anamnys.Server.Integrity.ITimestampAuthority>(Tsa);
        await using (var provider = services.BuildServiceProvider())
        {
            await DossierWorker.RunAsync(job!, provider.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(Now), NullLogger.Instance, Ct);
        }
        await using var db = s.Db.CreateDbContext();

        // Act
        var list = await ExecuteAsync(http => DossierEndpoints.ListAsync(http, db, s.Provider, s.Patient, 1, Now, Ct));
        var download = await ExecuteAsync(http => DossierEndpoints.DownloadAsync(http, db, store, s.Provider, job!.DossierId, Now, Ct));
        var receipt = await ExecuteAsync(http => DossierEndpoints.ReceiptAsync(http, db, s.Provider, job!.DossierId, Now, Ct));
        var token = await ExecuteAsync(http => DossierEndpoints.TokenAsync(http, db, s.Provider, job!.DossierId, Now, Ct));
        var expired = await ExecuteAsync(http => DossierEndpoints.DownloadAsync(http, db, store, s.Provider, job!.DossierId, Now.AddDays(7), Ct));

        // Assert
        list.Status.Should().Be(StatusCodes.Status200OK);
        list.CacheControl.Should().Be("no-store");
        using var page = JsonDocument.Parse(list.Body);
        page.RootElement.GetProperty("total").GetInt32().Should().Be(1);
        page.RootElement.GetProperty("page").GetInt32().Should().Be(1);
        page.RootElement.GetProperty("pageSize").GetInt32().Should().Be(5);
        var item = page.RootElement.GetProperty("items")[0];
        item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "id", "number", "status", "reason", "periodFrom", "periodTo", "requestedAt", "generatedAt", "expiresAt", "sizeBytes",
            "anchored", "integrityVerified");
        item.GetProperty("number").GetInt32().Should().Be(1);
        item.GetProperty("status").GetString().Should().Be("done");
        item.GetProperty("reason").GetString().Should().Be("other");
        item.GetProperty("periodFrom").GetString().Should().Be("2026-10-01");
        item.GetProperty("periodTo").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("anchored").GetBoolean().Should().BeTrue();
        item.GetProperty("integrityVerified").GetBoolean().Should().BeTrue();
        list.Body.Should().NotContain("Pedido da escola").And.NotContain("Benedita");

        download.Status.Should().Be(StatusCodes.Status200OK);
        download.CacheControl.Should().Be("no-store");
        using var link = JsonDocument.Parse(download.Body);
        link.RootElement.GetProperty("url").GetString().Should().Contain("name=anamnys-dossie-2026-11-03-001.zip");
        link.RootElement.GetProperty("expiresAt").GetDateTimeOffset().Should().Be(Now.AddHours(1));

        receipt.Status.Should().Be(StatusCodes.Status200OK);
        receipt.CacheControl.Should().Be("no-store");
        receipt.ContentType.Should().Be("application/pdf");
        receipt.ContentDisposition.Should().Be("attachment; filename=\"anamnys-dossie-001-comprovante.pdf\"");
        System.Text.Encoding.ASCII.GetString(receipt.Bytes, 0, 4).Should().Be("%PDF");

        token.Status.Should().Be(StatusCodes.Status200OK);
        token.CacheControl.Should().Be("no-store");
        token.ContentType.Should().Be("application/octet-stream");
        token.ContentDisposition.Should().Be("attachment; filename=\"anamnys-dossie-001.tsr\"");
        await using (var read = s.Db.CreateDbContext())
        {
            var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
                read.Dossiers, x => x.Id == job!.DossierId, Ct);
            token.Bytes.Should().Equal(row.AnchorToken);
            Tsa.VerifyDigest(token.Bytes, System.Security.Cryptography.SHA256.HashData(store.Objects[row.ObjectKey!])).Should().NotBeNull();
        }
        (await s.Db.CountAsync(
            """SELECT count(*) FROM "AuditEntries" WHERE "Action" = 'dossier_receipt_downloaded' AND "NewValue" = @v""",
            Ct, ("v", $"{job!.DossierId} tsr"))).Should().Be(1);

        expired.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(expired).Should().Be("expired");
    }

    [Fact]
    public async Task DownloadAndReceipt_Queued_Answer409NotReady()
    {
        // Arrange
        await using var s = await SceneAsync();
        var queue = new DossierQueue();
        await RequestAsync(s, queue, s.Provider, s.Patient, new DossierRequestBody("crp", null, null, null));
        queue.Reader.TryRead(out var job).Should().BeTrue();
        await using var db = s.Db.CreateDbContext();

        // Act
        var download = await ExecuteAsync(http => DossierEndpoints.DownloadAsync(http, db, new FakeNoteFileStore(), s.Provider, job!.DossierId, Now, Ct));
        var receipt = await ExecuteAsync(http => DossierEndpoints.ReceiptAsync(http, db, s.Provider, job!.DossierId, Now, Ct));
        var token = await ExecuteAsync(http => DossierEndpoints.TokenAsync(http, db, s.Provider, job!.DossierId, Now, Ct));

        // Assert
        token.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(token).Should().Be("not_ready");
        token.CacheControl.Should().Be("no-store");
        download.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(download).Should().Be("not_ready");
        receipt.Status.Should().Be(StatusCodes.Status409Conflict);
        Code(receipt).Should().Be("not_ready");
        receipt.CacheControl.Should().Be("no-store");
    }

    private static string? Code(Answer answer)
    {
        using var body = JsonDocument.Parse(answer.Body);
        return body.RootElement.GetProperty("code").GetString();
    }

    private sealed record Answer(int Status, string? CacheControl, string? ContentType, string? ContentDisposition, byte[] Bytes)
    {
        public string Body => System.Text.Encoding.UTF8.GetString(Bytes);
    }

    // Runs a handler's result the way the pipeline does, against an in-memory response.
    private static async Task<Answer> ExecuteAsync(Func<HttpContext, Task<IResult>> handler)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        http.Response.Body = body;
        var result = await handler(http);
        await result.ExecuteAsync(http);
        return new Answer(
            http.Response.StatusCode, http.Response.Headers.CacheControl.ToString(), http.Response.ContentType,
            http.Response.Headers.ContentDisposition.ToString(), body.ToArray());
    }
}
