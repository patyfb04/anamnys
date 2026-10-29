using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Analysis;

public static class CaseAnalysisEndpoints
{
    public static void MapCaseAnalysisEndpoints(this RouteGroupBuilder phi)
    {
        var group = phi.MapGroup("providers/me");

        group.MapGet("patients/{patientId:guid}/analysis", async (
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
            return await CaseAnalysisQuery.ExecuteAsync(db, providerId, patientId, from, to, ct) is { } analysis
                ? Results.Ok(analysis)
                : Results.NotFound();
        });
    }
}
