using System.Net;
using System.Text.Json;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Integrity;
using Anamnys.Tests.Auth;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;
using static Anamnys.Tests.Integrity.IntegrityTestData;

namespace Anamnys.Tests.Integrity;

// design/specs/2026-11-03-integrity-chain-design.md §8, §10. The routes answer 401 without a session through the running
// app; the handlers' answers (shapes, 404, no-store, the audited report) are checked on a scratch database, since the dev
// database the signed-in tests reach may not have the F-45 migration applied.
[Collection(SharedAppHostCollection.Name)]
public class IntegrityEndpointTests(SharedAppHostFixture fixture)
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static IntegrityEndpointTests() => QuestPDF.Settings.License = LicenseType.Community;

    [Theory]
    [InlineData("/api/phi/notes/00000000-0000-0000-0000-000000000001/integrity")]
    [InlineData("/api/phi/documents/00000000-0000-0000-0000-000000000001/integrity")]
    [InlineData("/api/phi/providers/me/integrity")]
    [InlineData("/api/phi/providers/me/integrity/report.pdf")]
    public async Task Route_WithoutSession_Returns401(string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        // Act
        using var response = await client.GetAsync(path, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SubjectAsync_SignedNoteAndDocument_AnswerTheShapeWithNoStore()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var note = await SignedNoteAsync(s, provider, patient, Now, Ct);
        var document = await SignedDocumentAsync(s, provider, patient, Now, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var noteAnswer = await ExecuteAsync(http => IntegrityEndpoints.SubjectAsync(http, db, Tsa, provider, IntegritySeal.Note, note, Ct));
        var documentAnswer = await ExecuteAsync(http => IntegrityEndpoints.SubjectAsync(http, db, Tsa, provider, IntegritySeal.Document, document, Ct));

        // Assert
        foreach (var answer in new[] { noteAnswer, documentAnswer })
        {
            answer.Status.Should().Be(StatusCodes.Status200OK);
            answer.CacheControl.Should().Be("no-store");
            using var json = JsonDocument.Parse(answer.Body);
            json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("status", "sealedAt", "retroactive", "anchored");
            json.RootElement.GetProperty("status").GetString().Should().Be("intact");
            json.RootElement.GetProperty("sealedAt").GetDateTimeOffset().Should().Be(Now);
            json.RootElement.GetProperty("retroactive").GetBoolean().Should().BeFalse();
            json.RootElement.GetProperty("anchored").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task SubjectAsync_ForeignUnknownOrDraft_Returns404WithNoStore()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var owner = await s.AddProviderAsync(Ct, withCrp: true);
        var stranger = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(owner, "Ana", Ct);
        var note = await SignedNoteAsync(s, owner, patient, Now, Ct);
        var document = await SignedDocumentAsync(s, owner, patient, Now, Ct);
        var draft = await AddReadyNoteAsync(s, owner, patient, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var answers = new[]
        {
            await ExecuteAsync(http => IntegrityEndpoints.SubjectAsync(http, db, Tsa, stranger, IntegritySeal.Note, note, Ct)),
            await ExecuteAsync(http => IntegrityEndpoints.SubjectAsync(http, db, Tsa, stranger, IntegritySeal.Document, document, Ct)),
            await ExecuteAsync(http => IntegrityEndpoints.SubjectAsync(http, db, Tsa, owner, IntegritySeal.Note, Guid.NewGuid(), Ct)),
            await ExecuteAsync(http => IntegrityEndpoints.SubjectAsync(http, db, Tsa, owner, IntegritySeal.Document, note, Ct)),
            await ExecuteAsync(http => IntegrityEndpoints.SubjectAsync(http, db, Tsa, owner, IntegritySeal.Note, draft, Ct)),
        };

        // Assert
        answers.Should().OnlyContain(a => a.Status == StatusCodes.Status404NotFound && a.CacheControl == "no-store");
    }

    [Fact]
    public async Task ChainAsync_AnswersTheSummaryShape_AndUnknownProviderIs404()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var note = await SignedNoteAsync(s, provider, patient, Now, Ct);
        using var fake = new FakeTimestampAuthority();
        await using var db = s.CreateDbContext();
        await IntegrityAnchoring.AnchorAsync(db, provider, fake, Ct);
        await s.ExecuteAsync("""UPDATE "NoteSections" SET "Content" = 'alterado' WHERE "NoteId" = @n AND "Key" = 'data'""", Ct, ("n", note));

        // Act
        var answer = await ExecuteAsync(http => IntegrityEndpoints.ChainAsync(http, db, fake, provider, Now, Ct));
        var unknown = await ExecuteAsync(http => IntegrityEndpoints.ChainAsync(http, db, fake, Guid.NewGuid(), Now, Ct));

        // Assert
        answer.Status.Should().Be(StatusCodes.Status200OK);
        answer.CacheControl.Should().Be("no-store");
        using var json = JsonDocument.Parse(answer.Body);
        var root = json.RootElement;
        root.EnumerateObject().Select(p => p.Name).Should().Equal("totalSeals", "discarded", "firstProblem", "lastAnchor", "verifiedAt");
        root.GetProperty("totalSeals").GetInt64().Should().Be(1);
        root.GetProperty("discarded").GetInt64().Should().Be(0);
        root.GetProperty("firstProblem").EnumerateObject().Select(p => p.Name).Should().Equal("sequence", "subjectType", "kind");
        root.GetProperty("firstProblem").GetProperty("kind").GetString().Should().Be("content_changed");
        root.GetProperty("firstProblem").GetProperty("subjectType").GetString().Should().Be("note");
        root.GetProperty("lastAnchor").EnumerateObject().Select(p => p.Name).Should().Equal("sequence", "anchoredAt", "tsaUrl");
        root.GetProperty("lastAnchor").GetProperty("tsaUrl").GetString().Should().Be(FakeTimestampAuthority.FakeUrl);
        root.GetProperty("verifiedAt").GetDateTimeOffset().Should().Be(Now);
        answer.Body.Should().NotContain("alterado");
        unknown.Status.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task ChainAsync_EmptyChain_HasNullProblemAndAnchor()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        await using var db = s.CreateDbContext();

        // Act
        var answer = await ExecuteAsync(http => IntegrityEndpoints.ChainAsync(http, db, Tsa, provider, Now, Ct));

        // Assert
        using var json = JsonDocument.Parse(answer.Body);
        json.RootElement.GetProperty("totalSeals").GetInt64().Should().Be(0);
        json.RootElement.GetProperty("firstProblem").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("lastAnchor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ReportAsync_RendersThePdf_AndIsAudited()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await SignedNoteAsync(s, provider, patient, Now, Ct);
        await SignedDocumentAsync(s, provider, patient, Now, Ct);
        using var fake = new FakeTimestampAuthority();
        await using var db = s.CreateDbContext();
        await IntegrityAnchoring.AnchorAsync(db, provider, fake, Ct);
        var head = (await SealsAsync(s, provider, Ct))[^1];

        // Act
        var answer = await ExecuteAsync(http => IntegrityEndpoints.ReportAsync(http, db, fake, provider, Now, Ct));
        var stranger = await ExecuteAsync(http => IntegrityEndpoints.ReportAsync(http, db, fake, Guid.NewGuid(), Now, Ct));

        // Assert
        answer.Status.Should().Be(StatusCodes.Status200OK);
        answer.CacheControl.Should().Be("no-store");
        answer.ContentType.Should().Be("application/pdf");
        answer.ContentDisposition.Should().Be("attachment; filename=\"comprovante-integridade-2026-11-03.pdf\"");
        answer.Bytes[..5].Should().Equal("%PDF-"u8.ToArray());
        using (var pdf = PdfDocument.Open(answer.Bytes))
        {
            // Spaces are not reliable in extracted PDF text: compare without them.
            var text = string.Concat(pdf.GetPages().Select(p => p.Text)).Replace(" ", "");
            text.Should().Contain("COMPROVANTEDEINTEGRIDADE");
            text.Should().Contain("Dra.Teste");
            text.Should().Contain("CRP123456/06");
            text.Should().Contain("íntegro");
            text.Should().Contain(head.Hash);
            text.Should().Contain(FakeTimestampAuthority.FakeUrl);
            text.Should().Contain("válido");
            text.Should().Contain("opensslts-verify");
            text.Should().NotContain("AnaSilva");
        }
        (await s.CountAsync(
            """
            SELECT count(*) FROM "AuditEntries"
            WHERE "ActorId" = @p AND "Action" = 'integrity_report_downloaded' AND "NoteId" IS NULL AND "DocumentId" IS NULL AND "NewValue" = '2 intact'
            """, Ct, ("p", provider))).Should().Be(1);
        stranger.Status.Should().Be(StatusCodes.Status404NotFound);
        (await s.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "Action" = 'integrity_report_downloaded'""", Ct)).Should().Be(1);
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
