using Anamnys.Server.Data;
using Anamnys.Server.Email;
using Anamnys.Server.Patients;
using Anamnys.Server.Storage;

namespace Anamnys.Server.RecordingConsents;

// design/specs/2026-11-15-recording-consent-design.md §5. PHI group (401 before anything), provider-scoped, 404 for another
// provider's patient or consent, every answer Cache-Control: no-store. Handlers are thin: resolve the provider from the
// session, delegate, map the outcome.
public static class RecordingConsentEndpoints
{
    public static void MapRecordingConsentEndpoints(this RouteGroupBuilder phi)
    {
        var me = phi.MapGroup("providers/me");

        me.MapGet("patients/{patientId:guid}/recording-consent", async (
            Guid patientId, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await GetAsync(http, db, providerId, patientId, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapGet("patients/{patientId:guid}/recording-consent/preview", async (
            Guid patientId, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await PreviewAsync(http, db, providerId, patientId, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("patients/{patientId:guid}/recording-consent", async (
            Guid patientId, SendConsentRequest? body, HttpContext http, AnamnysDbContext db, IConfiguration configuration, TimeProvider clock,
            CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await SendAsync(http, db, http.RequestServices.GetService<IEmailSender>(),
                    PatientInvitationEndpoints.PortalBaseUrl(http, configuration), providerId, patientId, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("recording-consents/{id:guid}/resend", async (
            Guid id, HttpContext http, AnamnysDbContext db, IConfiguration configuration, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await ResendAsync(http, db, http.RequestServices.GetService<IEmailSender>(),
                    PatientInvitationEndpoints.PortalBaseUrl(http, configuration), providerId, id, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("recording-consents/{id:guid}/renew", async (
            Guid id, HttpContext http, AnamnysDbContext db, IConfiguration configuration, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await RenewAsync(http, db, http.RequestServices.GetService<IEmailSender>(),
                    PatientInvitationEndpoints.PortalBaseUrl(http, configuration), providerId, id, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("recording-consents/{id:guid}/assent", async (
            Guid id, ConsentAssentRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await AssentAsync(http, db, providerId, id, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapPost("recording-consents/{id:guid}/revoke", async (
            Guid id, ConsentRevokeRequest? body, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await RevokeAsync(http, db, providerId, id, body, clock.GetUtcNow(), ct)
                : Results.Unauthorized());

        me.MapGet("recording-consents/{id:guid}/document", async (
            Guid id, HttpContext http, AnamnysDbContext db, INoteFileStore store, ILoggerFactory loggers, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? await DocumentAsync(http, db, store, loggers.CreateLogger(typeof(RecordingConsentEndpoints)), providerId, id, ct)
                : Results.Unauthorized());
    }

    internal static async Task<IResult> GetAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return await RecordingConsentService.GetViewAsync(db, providerId, patientId, now, ct) is { } view
            ? Results.Ok(view)
            : Results.NotFound();
    }

    internal static async Task<IResult> PreviewAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return await RecordingConsentService.PreviewAsync(db, providerId, patientId, now, ct) is { } preview
            ? Results.Ok(preview)
            : Results.NotFound();
    }

    internal static async Task<IResult> SendAsync(
        HttpContext http, AnamnysDbContext db, IEmailSender? sender, Uri portalBaseUrl, Guid providerId, Guid patientId,
        SendConsentRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        var result = await RecordingConsentService.SendAsync(db, sender, portalBaseUrl, providerId, patientId, body?.ProviderNote, now, ct);
        return result.Outcome == ConsentOutcome.Ok
            ? Results.Json(result.View, statusCode: StatusCodes.Status201Created)
            : Problem(result.Outcome);
    }

    internal static async Task<IResult> ResendAsync(
        HttpContext http, AnamnysDbContext db, IEmailSender? sender, Uri portalBaseUrl, Guid providerId, Guid id, DateTimeOffset now,
        CancellationToken ct)
    {
        NoStore(http);
        return Answer(await RecordingConsentService.ResendAsync(db, sender, portalBaseUrl, providerId, id, now, ct));
    }

    internal static async Task<IResult> RenewAsync(
        HttpContext http, AnamnysDbContext db, IEmailSender? sender, Uri portalBaseUrl, Guid providerId, Guid id, DateTimeOffset now,
        CancellationToken ct)
    {
        NoStore(http);
        var result = await RecordingConsentService.RenewAsync(db, sender, portalBaseUrl, providerId, id, now, ct);
        return result.Outcome == ConsentOutcome.Ok
            ? Results.Json(result.View, statusCode: StatusCodes.Status201Created)
            : Problem(result.Outcome);
    }

    internal static async Task<IResult> AssentAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, ConsentAssentRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return Answer(await RecordingConsentService.RecordAssentAsync(db, providerId, id, body, now, ct));
    }

    internal static async Task<IResult> RevokeAsync(
        HttpContext http, AnamnysDbContext db, Guid providerId, Guid id, ConsentRevokeRequest? body, DateTimeOffset now, CancellationToken ct)
    {
        NoStore(http);
        return Answer(await RecordingConsentService.RevokeAsync(db, providerId, id, body?.Reason, now, ct));
    }

    // Streamed, not presigned: no link to the signed term exists outside this response.
    internal static async Task<IResult> DocumentAsync(
        HttpContext http, AnamnysDbContext db, INoteFileStore store, ILogger logger, Guid providerId, Guid id, CancellationToken ct)
    {
        NoStore(http);
        var document = await RecordingConsentService.DocumentAsync(db, store, logger, providerId, id, ct);
        if (document.Outcome != ConsentOutcome.Ok)
        {
            return Problem(document.Outcome);
        }
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{document.FileName}\"";
        return Results.Bytes(document.Content!, "application/pdf");
    }

    private static IResult Answer(ConsentResult result) =>
        result.Outcome == ConsentOutcome.Ok ? Results.Ok(result.View) : Problem(result.Outcome);

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    internal static IResult Problem(ConsentOutcome outcome) => outcome switch
    {
        ConsentOutcome.NotFound => Results.NotFound(),
        ConsentOutcome.ProfileIncomplete => Results.BadRequest(new
        {
            message = "Preencha seu nome e o CRP no perfil antes de enviar o termo.", code = "profile_incomplete",
        }),
        ConsentOutcome.PatientEmailMissing => Results.BadRequest(new
        {
            message = "Informe o e-mail de contato do paciente antes de enviar o termo.", code = "patient_email_missing",
        }),
        ConsentOutcome.PatientBirthDateMissing => Results.BadRequest(new
        {
            message = "Informe a data de nascimento do paciente antes de enviar o termo.", code = "patient_birthdate_missing",
        }),
        ConsentOutcome.PatientArchived => Results.BadRequest(new
        {
            message = "Paciente arquivado: o termo não pode ser enviado.", code = "patient_archived",
        }),
        ConsentOutcome.InvalidNote => Results.BadRequest(new { message = "O texto é longo demais.", code = "invalid_note" }),
        ConsentOutcome.InvalidDate => Results.BadRequest(new
        {
            message = "Informe a data do assentimento (entre o pedido do termo e hoje).", code = "invalid_date",
        }),
        ConsentOutcome.NotMinor => Results.BadRequest(new
        {
            message = "O assentimento só é registrado para paciente menor de 18 anos.", code = "not_minor",
        }),
        ConsentOutcome.ConsentActive => Results.Conflict(new
        {
            message = "Este paciente já tem um termo enviado ou assinado.", code = "consent_active",
        }),
        ConsentOutcome.NotOutdated => Results.Conflict(new
        {
            message = "Este termo já está na versão atual.", code = "not_outdated",
        }),
        ConsentOutcome.OutdatedVersion => Results.Conflict(new
        {
            message = "Este termo é de uma versão antiga. Envie o termo novamente.", code = "outdated_version",
        }),
        ConsentOutcome.InvalidState =>Results.Conflict(new
        {
            message = "Esta ação não é possível no estado atual do termo.", code = "invalid_state",
        }),
        ConsentOutcome.EmailNotConfigured => Results.Json(new
        {
            message = "O envio de e-mails não está configurado neste ambiente.", code = "email_not_configured",
        }, statusCode: StatusCodes.Status503ServiceUnavailable),
        ConsentOutcome.StorageUnavailable => Results.Json(new
        {
            message = "O armazenamento está indisponível. Tente novamente.", code = "storage_unavailable",
        }, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(new
        {
            message = "Não foi possível enviar o e-mail. Tente novamente.", code = "send_failed",
        }, statusCode: StatusCodes.Status502BadGateway),
    };
}
