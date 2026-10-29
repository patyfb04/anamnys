using Anamnys.Server.Data;
using Anamnys.Server.Patients;

namespace Anamnys.Server.Retention;

// Thin handlers: resolve the provider from the session, validate, delegate
// (design/specs/2026-10-28-retention-panel-design.md §3).
public static class RetentionEndpoints
{
    public static void MapRetentionEndpoints(this RouteGroupBuilder phi)
    {
        var group = phi.MapGroup("providers/me");

        group.MapGet("compliance", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await ComplianceQuery.GetAsync(db, providerId, DateTimeOffset.UtcNow, ct));
        });

        group.MapGet("settings/retention", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return Results.Ok(await RetentionSettings.GetAsync(db, providerId, ct));
        });

        group.MapPut("settings/retention", async (RetentionSettingsRequest request, HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
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
            await RetentionSettings.SaveAsync(db, providerId, request, ct);
            return Results.NoContent();
        });
    }
}
