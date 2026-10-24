using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Anamnys.Server.Storage;

namespace Anamnys.Server.Exports;

public static class ExportEndpoints
{
    public static void AddAccountExport(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ExportQueue>();
        builder.Services.AddScoped<IAccountExportBuilder, AccountExportBuilder>();
        builder.Services.AddHostedService<ExportSweep>();
        builder.Services.AddHostedService<ExportWorker>();
    }

    public static void MapExportEndpoints(this RouteGroupBuilder phi)
    {
        var exports = phi.MapGroup("providers/me/exports");

        exports.MapPost("", async (HttpContext http, AnamnysDbContext db, ExportQueue queue, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await AccountExportService.RequestAsync(db, providerId, DateTimeOffset.UtcNow, ct);
            if (result.Outcome == ExportRequestOutcome.InProgress)
            {
                return Results.Conflict(new { message = "Já existe uma exportação em andamento.", code = "export_in_progress" });
            }
            queue.Enqueue(new ExportJob(result.Id!.Value, providerId));
            return Results.Accepted(value: new { id = result.Id, status = result.Status });
        });

        exports.MapGet("", async (HttpContext http, AnamnysDbContext db, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await AccountExportService.LatestAsync(db, providerId, ct) is { } latest
                ? Results.Ok(latest)
                : Results.NoContent();
        });

        exports.MapPost("{id:guid}/download", async (Guid id, HttpContext http, AnamnysDbContext db, INoteFileStore store, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var result = await AccountExportService.DownloadAsync(db, store, providerId, id, DateTimeOffset.UtcNow, ct);
            return result.Outcome switch
            {
                ExportDownloadOutcome.Ok => Results.Ok(new { url = result.Url, expiresAt = result.ExpiresAt }),
                ExportDownloadOutcome.NotFound => Results.NotFound(),
                ExportDownloadOutcome.Expired => Results.Conflict(new { message = "Esta exportação expirou. Gere uma nova.", code = "expired" }),
                _ => Results.Conflict(new { message = "A exportação ainda não está pronta.", code = "not_ready" }),
            };
        });
    }
}
