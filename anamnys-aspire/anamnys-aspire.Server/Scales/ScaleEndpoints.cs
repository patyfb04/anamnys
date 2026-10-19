using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Scales;

public static class ScaleEndpoints
{
    public static void MapScaleEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapGet("providers/me/instruments", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await ScaleCatalog.ListAsync(db, providerId, ct));
        });

        phi.MapPost("providers/me/instruments/{id:guid}/opt-in", async (Guid id, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await ScaleCatalog.OptInAsync(db, providerId, id, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                OptInOutcome.OptedIn or OptInOutcome.AlreadyOptedIn => Results.NoContent(),
                OptInOutcome.NotFound => Results.NotFound(),
                OptInOutcome.NotAllowed => Results.Conflict(new { code = "opt_in_not_allowed", message = result.Explanation }),
            };
        });

        phi.MapGet("providers/me/patients/{id:guid}/scales", async (Guid id, Guid? sessionId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var series = await ScaleRecording.SeriesAsync(db, providerId, id, sessionId, ct);
            return series is null ? Results.NotFound() : Results.Ok(series);
        });

        phi.MapPost("providers/me/patients/{id:guid}/scales", async (Guid id, RecordScaleRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await ScaleRecording.RecordAsync(db, providerId, id, request, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                RecordOutcome.Recorded => Results.Json(new { id = result.Id }, statusCode: StatusCodes.Status201Created),
                RecordOutcome.Invalid => Results.ValidationProblem(result.Errors!),
                RecordOutcome.NotFound => Results.NotFound(),
                RecordOutcome.Archived => Results.Conflict(new { code = "archived", message = "Desarquive o paciente para registrar escalas." }),
                RecordOutcome.Unavailable => Results.Conflict(new { code = "instrument_unavailable", message = result.Explanation }),
                RecordOutcome.OptInRequired => Results.Conflict(new { code = "opt_in_required", message = result.Explanation }),
            };
        });

        phi.MapDelete("providers/me/patients/{id:guid}/scales/{applicationId:guid}", async (Guid id, Guid applicationId, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await ScaleRecording.RemoveAsync(db, providerId, id, applicationId, DateTimeOffset.UtcNow, ct)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }
}
