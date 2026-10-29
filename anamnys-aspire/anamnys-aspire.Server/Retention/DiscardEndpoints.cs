using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Anamnys.Server.Storage;

namespace Anamnys.Server.Retention;

public sealed record DiscardRequest(string? ConfirmName);

// Thin handlers for the discard and the disposal terms (design/specs/2026-10-28-retention-panel-design.md §3).
public static class DiscardEndpoints
{
    public static void MapDiscardEndpoints(this RouteGroupBuilder phi)
    {
        var group = phi.MapGroup("providers/me");

        group.MapPost("patients/{patientId:guid}/discard", async (
            Guid patientId, DiscardRequest request, HttpContext http, AnamnysDbContext db, INoteFileStore store,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await DiscardService.DiscardAsync(
                db, store, loggers.CreateLogger(typeof(DiscardService)), providerId, patientId, request.ConfirmName, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                DiscardOutcome.Ok => Results.Ok(new { termId = result.TermId, counts = result.Counts }),
                DiscardOutcome.NotFound => Results.NotFound(),
                DiscardOutcome.AlreadyDiscarded => Conflict("already_discarded", "Este prontuário já foi descartado."),
                DiscardOutcome.NotExpired => Conflict("not_expired", "O prazo de guarda deste prontuário ainda não terminou."),
                DiscardOutcome.FutureAppointment => Conflict("future_appointment",
                    "O paciente tem agendamento futuro. Cancele-o antes de descartar o prontuário."),
                DiscardOutcome.NameMismatch => Conflict("name_mismatch", "O nome digitado não confere com o nome do paciente."),
                _ => Results.Json(new { message = "Armazenamento indisponível. Tente de novo em instantes.", code = "storage_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            };
        });

        group.MapGet("disposal-terms", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await DisposalTerms.ListAsync(db, providerId, ct));
        });

        group.MapGet("disposal-terms/{termId:guid}/pdf", async (
            Guid termId, HttpContext http, AnamnysDbContext db, INoteFileStore store, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var link = await DisposalTerms.PdfLinkAsync(db, store, providerId, termId, DateTimeOffset.UtcNow, ct);
            return link is null ? Results.NotFound() : Results.Ok(new { url = link.Url, expiresAt = link.ExpiresAt });
        });
    }

    private static IResult Conflict(string code, string message) => Results.Conflict(new { message, code });
}
