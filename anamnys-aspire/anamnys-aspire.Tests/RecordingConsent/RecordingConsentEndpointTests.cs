using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Server.Data;
using Anamnys.Server.RecordingConsents;
using Anamnys.Server.Storage;
using Anamnys.Tests.Auth;
using Anamnys.Tests.Notes;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;
using static Anamnys.Tests.RecordingConsents.ConsentScene;

namespace Anamnys.Tests.RecordingConsents;

// design/specs/2026-11-15-recording-consent-design.md §5. The provider routes answer 401 without a session and the public
// routes 410 for an unknown link through the running app; the handlers' answers (codes, shapes, 404, no-store) are checked
// on a scratch database, since the dev database the running app reaches may not have the migration applied.
[Collection(SharedAppHostCollection.Name)]
public class RecordingConsentEndpointTests(SharedAppHostFixture fixture)
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string AnyId = "00000000-0000-0000-0000-000000000001";
    private const string UnknownToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    static RecordingConsentEndpointTests() => QuestPDF.Settings.License = LicenseType.Community;

    [Theory]
    [InlineData("GET", "providers/me/patients/" + AnyId + "/recording-consent")]
    [InlineData("GET", "providers/me/patients/" + AnyId + "/recording-consent/preview")]
    [InlineData("POST", "providers/me/patients/" + AnyId + "/recording-consent")]
    [InlineData("POST", "providers/me/recording-consents/" + AnyId + "/resend")]
    [InlineData("POST", "providers/me/recording-consents/" + AnyId + "/assent")]
    [InlineData("POST", "providers/me/recording-consents/" + AnyId + "/revoke")]
    [InlineData("POST", "providers/me/recording-consents/" + AnyId + "/renew")]
    [InlineData("GET", "providers/me/recording-consents/" + AnyId + "/document")]
    public async Task Route_WithoutSession_Returns401(string method, string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/phi/" + path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { });
        }

        // Act
        using var response = await client.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // A malformed token is refused before any database read, so this runs against the dev database as it is.
    [Theory]
    [InlineData("GET", "short")]
    [InlineData("POST", "short")]
    [InlineData("GET", "revoke/short")]
    [InlineData("POST", "revoke/short")]
    public async Task PublicRoute_MalformedLink_Returns410WithTheSameMessage(string method, string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/public/recording-consents/" + path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { birthDate = "1990-05-04", signerName = "X", accepted = true });
        }

        // Act
        using var response = await client.SendAsync(request, Ct);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("message").GetString().Should().Be(PublicConsentEndpoints.GoneMessage);
    }

    [Fact]
    public async Task PublicRoutes_CarryThePublicRateLimitPolicy_AndAllowNoAuthentication()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<AnamnysDbContext>(o => o.UseNpgsql("Host=unused"));
        builder.Services.AddSingleton<INoteFileStore, FakeNoteFileStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        await using var app = builder.Build();
        app.MapPublicConsentEndpoints();

        // Act
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/public/recording-consents"))
            .ToList();

        // Assert
        endpoints.Should().HaveCount(4);
        endpoints.Should().OnlyContain(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()!.PolicyName == "public");
        endpoints.Should().OnlyContain(e => e.Metadata.GetMetadata<IAuthorizeData>() == null);
    }

    [Fact]
    public async Task ProviderHandlers_AnswerTheirShapesAndCodes_AlwaysNoStore()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var none = await ExecuteAsync(http => RecordingConsentEndpoints.GetAsync(http, db, s.Provider, s.Adult, Now, Ct));
        var stranger = await ExecuteAsync(http => RecordingConsentEndpoints.GetAsync(http, db, s.Provider, s.Stranger, Now, Ct));
        var preview = await ExecuteAsync(http => RecordingConsentEndpoints.PreviewAsync(http, db, s.Provider, s.Adult, Now, Ct));
        var sent = await ExecuteAsync(http => RecordingConsentEndpoints.SendAsync(http, db, s.Sender, Portal, s.Provider, s.Adult, new(null), Now, Ct));
        var active = await ExecuteAsync(http => RecordingConsentEndpoints.SendAsync(http, db, s.Sender, Portal, s.Provider, s.Adult, new(null), Now, Ct));
        var noSender = await ExecuteAsync(http => RecordingConsentEndpoints.SendAsync(http, db, null, Portal, s.Provider, s.Minor, new(null), Now, Ct));
        using var sentJson = JsonDocument.Parse(sent.Body);
        var id = sentJson.RootElement.GetProperty("id").GetGuid();
        var noDocument = await ExecuteAsync(http => RecordingConsentEndpoints.DocumentAsync(http, db, s.Store, NullLogger.Instance, s.Provider, id, Ct));
        var notMinor = await ExecuteAsync(http => RecordingConsentEndpoints.AssentAsync(http, db, s.Provider, id, new(new DateOnly(2026, 11, 15), null), Now, Ct));
        var renew = await ExecuteAsync(http => RecordingConsentEndpoints.RenewAsync(http, db, s.Sender, Portal, s.Provider, id, Now, Ct));
        var signed = await ExecuteAsync(http => PublicConsentEndpoints.SignAsync(http, db, s.Store, s.Sender, Portal, NullLogger.Instance,
            s.SignToken(), new(AdultBirth, "Benedita Silva", null, true), SignedAt, Ct));
        var document = await ExecuteAsync(http => RecordingConsentEndpoints.DocumentAsync(http, db, s.Store, NullLogger.Instance, s.Provider, id, Ct));
        var resendSigned = await ExecuteAsync(http => RecordingConsentEndpoints.ResendAsync(http, db, s.Sender, Portal, s.Provider, id, Later, Ct));
        var revoke = await ExecuteAsync(http => RecordingConsentEndpoints.RevokeAsync(http, db, s.Provider, id, new("Pedido em sessão."), Later, Ct));
        var foreign = await ExecuteAsync(http => RecordingConsentEndpoints.RevokeAsync(http, db, s.OtherProvider, id, new(null), Later, Ct));

        // Assert
        none.Status.Should().Be(200);
        using (var json = JsonDocument.Parse(none.Body))
        {
            json.RootElement.GetProperty("state").GetString().Should().Be("none");
            json.RootElement.GetProperty("canRecord").GetBoolean().Should().BeFalse();
            json.RootElement.GetProperty("isMinor").GetBoolean().Should().BeFalse();
            json.RootElement.GetProperty("currentVersion").GetString().Should().Be("v1");
        }
        stranger.Status.Should().Be(404);
        preview.Status.Should().Be(200);
        using (var json = JsonDocument.Parse(preview.Body))
        {
            json.RootElement.GetProperty("text").GetString().Should().Contain("Benedita Silva");
            json.RootElement.GetProperty("sendBlockedBy").ValueKind.Should().Be(JsonValueKind.Null);
        }
        sent.Status.Should().Be(201);
        sentJson.RootElement.GetProperty("state").GetString().Should().Be("sent");
        Code(active).Should().Be((409, "consent_active"));
        Code(noSender).Should().Be((503, "email_not_configured"));
        noDocument.Status.Should().Be(404);
        Code(notMinor).Should().Be((400, "not_minor"));
        Code(renew).Should().Be((409, "not_outdated"));
        signed.Status.Should().Be(204);
        document.Status.Should().Be(200);
        System.Text.Encoding.ASCII.GetString(document.Body[..5]).Should().Be("%PDF-");
        Code(resendSigned).Should().Be((409, "invalid_state"));
        revoke.Status.Should().Be(200);
        foreign.Status.Should().Be(404);
        new[] { none, stranger, preview, sent, active, noSender, noDocument, notMinor, renew, signed, document, resendSigned, revoke, foreign }
            .Should().OnlyContain(a => a.CacheControl == "no-store");
    }

    [Fact]
    public async Task PublicHandlers_WrongBirthDateIs400_IncompleteBodyIs400_DeadLinkIs410()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SendAsync(s.Adult, Ct);
        var token = s.SignToken();
        await using var db = s.CreateDbContext();

        // Act
        var view = await ExecuteAsync(http => PublicConsentEndpoints.ViewSignAsync(http, db, token, Now, Ct));
        var incomplete = await ExecuteAsync(http => PublicConsentEndpoints.SignAsync(http, db, s.Store, s.Sender, Portal, NullLogger.Instance,
            token, new(null, null, null, false), SignedAt, Ct));
        var wrong = await ExecuteAsync(http => PublicConsentEndpoints.SignAsync(http, db, s.Store, s.Sender, Portal, NullLogger.Instance,
            token, new(new DateOnly(1999, 1, 1), "Benedita Silva", null, true), SignedAt, Ct));
        var late = await ExecuteAsync(http => PublicConsentEndpoints.ViewSignAsync(http, db, token, Now.AddDays(8), Ct));
        var unknownView = await ExecuteAsync(http => PublicConsentEndpoints.ViewSignAsync(http, db, UnknownToken, Now, Ct));
        var unknownRevokeView = await ExecuteAsync(http => PublicConsentEndpoints.ViewRevokeAsync(http, db, UnknownToken, Now, Ct));
        var unknownRevoke = await ExecuteAsync(http => PublicConsentEndpoints.RevokeAsync(http, db, UnknownToken, new(AdultBirth), Now, Ct));

        // Assert
        view.Status.Should().Be(200);
        using (var json = JsonDocument.Parse(view.Body))
        {
            json.RootElement.GetProperty("guardianExpected").GetBoolean().Should().BeFalse();
            json.RootElement.GetProperty("text").GetString().Should().NotContain("04/05/1990");
        }
        Code(incomplete).Should().Be((400, "invalid"));
        Code(wrong).Should().Be((400, "birthdate_mismatch"));
        foreach (var dead in new[] { late, unknownView, unknownRevokeView, unknownRevoke })
        {
            dead.Status.Should().Be(410);
            System.Text.Encoding.UTF8.GetString(dead.Body).Should().Be(System.Text.Encoding.UTF8.GetString(late.Body));
        }
        new[] { view, incomplete, wrong, late }.Should().OnlyContain(a => a.CacheControl == "no-store");
    }

    private static (int, string?) Code(Answer answer)
    {
        using var json = JsonDocument.Parse(answer.Body);
        return (answer.Status, json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private sealed record Answer(int Status, string CacheControl, byte[] Body);

    private static async Task<Answer> ExecuteAsync(Func<HttpContext, Task<IResult>> handler)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        http.Response.Body = body;
        var result = await handler(http);
        await result.ExecuteAsync(http);
        return new Answer(http.Response.StatusCode, http.Response.Headers.CacheControl.ToString(), body.ToArray());
    }
}
