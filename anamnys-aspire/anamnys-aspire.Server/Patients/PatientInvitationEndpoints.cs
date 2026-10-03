using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Anamnys.Server.Email;
using Microsoft.AspNetCore.Authentication;

namespace Anamnys.Server.Patients;

public sealed record AcceptInvitationRequest(string? Token);

// See design/specs/2026-10-03-portal-invitation-design.md §4.
public static class PatientInvitationEndpoints
{
    public static void MapPatientInvitationEndpoints(this RouteGroupBuilder phi)
    {
        var record = phi.MapGroup("providers/me/patients/{patientId:guid}");

        record.MapPost("invitation", async (Guid patientId, HttpContext http, AnamnysDbContext db, IConfiguration configuration, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }

            // Optional by design: without Email:Provider there is no sender (see AddEmail).
            var sender = http.RequestServices.GetService<IEmailSender>();
            var outcome = await PatientInvitations.InviteAsync(
                db, providerId, patientId, sender, PortalBaseUrl(http, configuration), DateTimeOffset.UtcNow, ct);

            return outcome switch
            {
                InviteOutcome.Sent => Results.NoContent(),
                InviteOutcome.NotFound => Results.NotFound(),
                InviteOutcome.Archived => Conflict("Paciente arquivado não pode ser convidado. Reative-o primeiro."),
                InviteOutcome.AlreadyActive => Conflict("Este paciente já tem acesso ao portal."),
                InviteOutcome.MissingContactEmail => Conflict("Informe o e-mail de contato do paciente antes de convidar."),
                InviteOutcome.EmailNotConfigured => Results.Problem(
                    "O envio de e-mails não está configurado neste ambiente.", statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Problem(
                    "Não foi possível enviar o convite. Tente novamente.", statusCode: StatusCodes.Status502BadGateway),
            };
        });

        record.MapDelete("invitation", async (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? (await PatientInvitations.CancelAsync(db, providerId, patientId, DateTimeOffset.UtcNow, ct) ? Results.NoContent() : Results.NotFound())
                : Results.Unauthorized());

        record.MapDelete("portal-access", async (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
            await PatientRecordEndpoints.ProviderIdAsync(http) is { } providerId
                ? (await PatientInvitations.RemoveAccessAsync(db, providerId, patientId, ct) ? Results.NoContent() : Results.NotFound())
                : Results.Unauthorized());

        // Patient realm only: a provider session is not a patient account.
        phi.MapPost("patients/me/invitations/accept", async (AcceptInvitationRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            var auth = await http.AuthenticateAsync(AuthSchemes.PatientCookie);
            if (!auth.Succeeded || auth.Principal!.LocalIdOrNull() is not { } accountId)
            {
                return Results.Unauthorized();
            }

            var result = string.IsNullOrWhiteSpace(request.Token)
                ? new AcceptResult(AcceptOutcome.Invalid)
                : await PatientInvitations.AcceptAsync(db, accountId, request.Token.Trim(), DateTimeOffset.UtcNow, ct);

            return result.Outcome switch
            {
                AcceptOutcome.Accepted => Results.Ok(new { providerName = result.ProviderName }),
                AcceptOutcome.EmailMismatch => Results.BadRequest(new { code = "email_mismatch" }),
                AcceptOutcome.AlreadyLinked => Results.BadRequest(new { code = "already_linked" }),
                _ => Results.BadRequest(new { code = "invalid" }),
            };
        });
    }

    private static IResult Conflict(string message) => Results.Conflict(new { message });

    // PatientPortal:BaseUrl in dev (the patient app's own Vite origin); otherwise the
    // patient app is served same-origin under /patient.
    private static Uri PortalBaseUrl(HttpContext http, IConfiguration configuration) =>
        configuration["PatientPortal:BaseUrl"] is { Length: > 0 } configured
            ? new Uri(configured)
            : new Uri($"{http.Request.Scheme}://{http.Request.Host}/patient");
}
