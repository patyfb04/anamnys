using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Analysis;

// F-14 (design/specs/2026-10-30-plan-adherence-design.md §3). A patient, note or follow-up of another provider, or of a
// discarded record, is 404; an archived record is read-only (409 code "archived").
public static class PlanAdherenceEndpoints
{
    public static void MapPlanAdherenceEndpoints(this RouteGroupBuilder phi)
    {
        var group = phi.MapGroup("providers/me");

        group.MapGet("patients/{patientId:guid}/plan-adherence", async (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await PlanAdherenceQuery.ExecuteAsync(db, providerId, patientId, ct) is { } adherence
                ? Results.Ok(adherence)
                : Results.NotFound();
        });

        group.MapPost("patients/{patientId:guid}/plan/review", async (Guid patientId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await PlanAdherenceService.ReviewAsync(db, providerId, patientId, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                PlanOutcome.Ok => Results.Ok(new { planId = result.PlanId, reviewedAt = result.ReviewedAt }),
                PlanOutcome.Archived => Archived(),
                _ => Results.NotFound(),
            };
        });

        group.MapPost("notes/{noteId:guid}/follow-ups", async (Guid noteId, FollowUpRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var result = await PlanAdherenceService.CreateFollowUpAsync(db, providerId, noteId, request, DateTimeOffset.UtcNow, ct);
            return result.Outcome == FollowUpOutcome.Ok
                ? Results.Json(new CreatedResponse(result.Id!.Value), statusCode: StatusCodes.Status201Created)
                : ToResult(result.Outcome);
        });

        group.MapPost("follow-ups/{id:guid}/resolve", async (Guid id, ResolveFollowUpRequest? request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return ToResult(await PlanAdherenceService.ResolveFollowUpAsync(db, providerId, id, request?.NoteId, DateTimeOffset.UtcNow, ct));
        });

        group.MapPost("follow-ups/{id:guid}/reopen", async (Guid id, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return ToResult(await PlanAdherenceService.ReopenFollowUpAsync(db, providerId, id, DateTimeOffset.UtcNow, ct));
        });
    }

    private static IResult Archived() =>
        Results.Conflict(new { message = "Paciente arquivado: o prontuário é somente leitura.", code = "archived" });

    private static IResult ToResult(FollowUpOutcome outcome) => outcome switch
    {
        FollowUpOutcome.Ok => Results.NoContent(),
        FollowUpOutcome.Archived => Archived(),
        FollowUpOutcome.NoteNotReady => Results.Conflict(new { message = "Só é possível marcar pontos em notas em revisão ou assinadas.", code = "note_not_ready" }),
        FollowUpOutcome.InvalidSelection => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["offsetStart"] = ["O trecho selecionado não corresponde ao texto atual da nota."] },
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_selection" }),
        FollowUpOutcome.InvalidNote => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["noteId"] = ["Escolha uma nota posterior deste paciente."] },
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_note" }),
        _ => Results.NotFound(),
    };
}
