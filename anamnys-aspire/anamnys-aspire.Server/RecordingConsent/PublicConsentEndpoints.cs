using Anamnys.Server.Data;
using Anamnys.Server.Email;
using Anamnys.Server.Notifications;
using Anamnys.Server.Patients;
using Anamnys.Server.Storage;

namespace Anamnys.Server.RecordingConsents;

// The signing and revocation links (design/specs/2026-11-15-recording-consent-design.md §4). Anonymous: the token is the
// credential, and only its SHA-256 is looked up. Rate limited by the public policy. Every dead, unknown, used or expired
// link answers the same 410.
public static class PublicConsentEndpoints
{
    public const string GoneMessage = "Este link não é mais válido.";

    public static void MapPublicConsentEndpoints(this WebApplication app)
    {
        var consents = app.MapGroup("/api/public/recording-consents").RequireRateLimiting(ConfirmationEndpoints.RateLimitPolicy);

        consents.MapGet("revoke/{token}", async (string token, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await ViewRevokeAsync(http, db, token, clock.GetUtcNow(), ct));

        consents.MapPost("revoke/{token}", async (
            string token, PublicConsentRevokeRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await RevokeAsync(http, db, token, body, clock.GetUtcNow(), ct));

        consents.MapGet("{token}", async (string token, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await ViewSignAsync(http, db, token, clock.GetUtcNow(), ct));

        consents.MapPost("{token}", async (
            string token, PublicConsentSignRequest? body, HttpContext http, AnamnysDbContext db, INoteFileStore store, IConfiguration configuration,
            IHostEnvironment environment, ILoggerFactory loggers, TimeProvider clock, CancellationToken ct) =>
            await SignAsync(http, db, store, http.RequestServices.GetService<IEmailSender>(), PortalBaseUrl(http, configuration, environment),
                loggers.CreateLogger(typeof(PublicConsentEndpoints)), token, body, clock.GetUtcNow(), ct));
    }

    // The base of the revocation link e-mailed after an anonymous request: the configured portal URL only, so the caller's
    // Host header never shapes the link. Development falls back to the request's host (the dev portal has no setting).
    // Outside Development the server does not start without an absolute https setting (ShareNetwork.RequirePortalUrl in
    // Program.cs, and the e-mail sender gate), so the null branch is only a last guard: no link, the copy e-mail skipped
    // and logged.
    internal static Uri? PortalBaseUrl(HttpContext http, IConfiguration configuration, IHostEnvironment environment) =>
        configuration["PatientPortal:BaseUrl"] is { Length: > 0 } configured ? new Uri(configured)
        : environment.IsDevelopment() ? PatientInvitationEndpoints.PortalBaseUrl(http, configuration)
        : null;

    internal static async Task<IResult> ViewSignAsync(HttpContext http, AnamnysDbContext db, string token, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return await RecordingConsentService.ViewSignAsync(db, token, now, ct) is { } view ? Results.Ok(view) : Gone();
    }

    internal static async Task<IResult> SignAsync(
        HttpContext http, AnamnysDbContext db, INoteFileStore store, IEmailSender? sender, Uri? portalBaseUrl, ILogger logger, string token,
        PublicConsentSignRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return Answer(await RecordingConsentService.SignAsync(db, store, sender, portalBaseUrl, logger, token, body, now, ct));
    }

    internal static async Task<IResult> ViewRevokeAsync(HttpContext http, AnamnysDbContext db, string token, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return await RecordingConsentService.ViewRevokeAsync(db, token, now, ct) is { } view ? Results.Ok(view) : Gone();
    }

    internal static async Task<IResult> RevokeAsync(
        HttpContext http, AnamnysDbContext db, string token, PublicConsentRevokeRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return Answer(await RecordingConsentService.RevokeByLinkAsync(db, token, body, now, ct));
    }

    private static IResult Answer(PublicConsentResult result) => result.Outcome switch
    {
        PublicConsentOutcome.Ok => Results.NoContent(),
        PublicConsentOutcome.Invalid => Results.BadRequest(new
        {
            message = "Preencha todos os campos e marque \"Li e concordo\".", code = "invalid", fields = result.Errors,
        }),
        PublicConsentOutcome.BirthDateMismatch => Results.BadRequest(new
        {
            message = "A data de nascimento não confere com o cadastro.", code = "birthdate_mismatch",
        }),
        PublicConsentOutcome.StorageUnavailable => Results.Json(new
        {
            message = "Não foi possível registrar agora. Tente novamente em alguns minutos.", code = "unavailable",
        }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Gone(),
    };

    private static IResult Gone() => Results.Json(new { message = GoneMessage }, statusCode: StatusCodes.Status410Gone);

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
}
