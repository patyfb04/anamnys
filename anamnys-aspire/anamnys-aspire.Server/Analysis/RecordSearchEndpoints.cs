using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Analysis;

// F-16 (design/specs/2026-10-31-record-search-design.md §3). A POST so the question stays out of URLs and access logs; it
// is never logged or audited. Another provider's or a discarded patient is 404; an archived one can search. Nothing
// found is 200 with no passages.
public static class RecordSearchEndpoints
{
    public static void MapRecordSearchEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapPost("providers/me/patients/{patientId:guid}/record-search", async (
            Guid patientId, RecordSearchRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
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
            return await RecordSearchQuery.ExecuteAsync(db, providerId, patientId, request.Question!, request.From, request.To, ct, request.Page ?? 1) is { } result
                ? Results.Ok(result)
                : Results.NotFound();
        });
    }
}
