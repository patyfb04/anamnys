using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Medications;

public static class MedicationEndpoints
{
    public static void MapMedicationEndpoints(this RouteGroupBuilder phi)
    {
        var group = phi.MapGroup("providers/me");

        group.MapGet("external-documents/{id:guid}/facts/{factId:guid}/medication-draft", async (
            Guid id, Guid factId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await MedicationFacts.DraftAsync(db, providerId, id, factId, ct);
            return result.Draft is { } draft ? Results.Ok(draft) : Failure(result.Outcome);
        });

        group.MapPost("external-documents/{id:guid}/facts/{factId:guid}/medication", async (
            Guid id, Guid factId, MedicationFromFactRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var now = DateTimeOffset.UtcNow;
            var result = await MedicationFacts.RecordAsync(db, providerId, id, factId, request, PatientSearchQuery.PracticeToday(now), now, ct);
            return result.Outcome == MedicationFactOutcome.Ok
                ? Results.Json(new CreatedResponse(result.MedicationId!.Value), statusCode: StatusCodes.Status201Created)
                : Failure(result.Outcome, result.Errors);
        });

        group.MapGet("patients/{patientId:guid}/timeline", async (
            Guid patientId, DateOnly? from, DateOnly? to, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            if (from > to)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["O início do período deve ser anterior ao fim."] });
            }
            return await MedicationTimeline.GetAsync(db, providerId, patientId, from, to, ct) is { } timeline
                ? Results.Ok(timeline)
                : Results.NotFound();
        });
    }

    private static IResult Failure(MedicationFactOutcome outcome, Dictionary<string, string[]>? errors = null) => outcome switch
    {
        MedicationFactOutcome.NotFound => Results.NotFound(),
        MedicationFactOutcome.Archived => Results.Conflict(new { message = "Paciente arquivado: o prontuário é somente leitura.", code = "archived" }),
        MedicationFactOutcome.NotPending => Results.Conflict(new { message = "Este fato foi descartado.", code = "not_pending" }),
        MedicationFactOutcome.NotMedication => Results.Conflict(new { message = "Este fato não é um medicamento.", code = "not_medication" }),
        MedicationFactOutcome.AlreadyRecorded => Results.Conflict(new { message = "Este fato já está no prontuário.", code = "already_recorded" }),
        MedicationFactOutcome.TargetNotActive => Results.Conflict(new { message = "O medicamento escolhido já foi encerrado.", code = "target_not_active" }),
        _ => Results.ValidationProblem(errors ?? new Dictionary<string, string[]> { ["request"] = ["Dados inválidos."] }),
    };
}
