using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notifications;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Exports;

// One export at a time. Failures are logged by exception type only: the build handles patient data.
public sealed class ExportWorker(
    ExportQueue queue, IServiceScopeFactory scopes, TimeProvider clock, ILogger<ExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DeleteLeftoverTempFiles(Path.GetTempPath(), clock.GetUtcNow(), logger);
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await RunAsync(job, scopes, clock, logger, stoppingToken);
        }
    }

    // Best effort: temp ZIPs a previous process left behind (a crash skips DeleteOnClose).
    internal static int DeleteLeftoverTempFiles(string directory, DateTimeOffset startedAt, ILogger logger)
    {
        var deleted = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "anamnys-export-*.zip"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < startedAt.UtcDateTime)
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                catch (Exception e)
                {
                    logger.LogWarning("Could not delete a leftover export temp file: {ExceptionType}", e.GetType().Name);
                }
            }
        }
        catch (Exception e)
        {
            logger.LogWarning("Could not scan for leftover export temp files: {ExceptionType}", e.GetType().Name);
        }
        return deleted;
    }

    internal static async Task RunAsync(
        ExportJob job, IServiceScopeFactory scopes, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        string? objectKey = null;
        INoteFileStore? store = null;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            store = scope.ServiceProvider.GetRequiredService<INoteFileStore>();
            var builder = scope.ServiceProvider.GetRequiredService<IAccountExportBuilder>();

            var started = await db.AccountExports
                .Where(x => x.Id == job.ExportId && x.ProviderId == job.ProviderId && x.Status == AccountExportService.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AccountExportService.Running), ct);
            if (started == 0)
            {
                return;
            }

            await using var file = new FileStream(
                Path.Combine(Path.GetTempPath(), $"anamnys-export-{Guid.NewGuid():N}.zip"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await builder.BuildAsync(job.ProviderId, file, ct);
            await file.FlushAsync(ct);
            var size = file.Length;
            file.Position = 0;

            objectKey = AccountExportService.ObjectKeyFor(job.ProviderId, job.ExportId);
            await store.PutAsync(objectKey, file, "application/zip", ct);
            if (!await AccountExportService.CompleteAsync(db, job, objectKey, size, clock.GetUtcNow(), ct))
            {
                await DeleteBestEffortAsync(store, objectKey, logger);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Account export {ExportId} failed: {ExceptionType}", job.ExportId, e.GetType().Name);
            if (store is not null && objectKey is not null)
            {
                await DeleteBestEffortAsync(store, objectKey, logger);
            }
            await FailBestEffortAsync(job, scopes, logger);
        }
    }

    private static async Task FailBestEffortAsync(ExportJob job, IServiceScopeFactory scopes, ILogger logger)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            await db.AccountExports
                .Where(x => x.Id == job.ExportId && x.ProviderId == job.ProviderId && x.Status == AccountExportService.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, AccountExportService.Failed)
                    .SetProperty(x => x.FailureReason, "export_error"));
        }
        catch (Exception e)
        {
            logger.LogError("Could not mark account export {ExportId} as failed: {ExceptionType}", job.ExportId, e.GetType().Name);
        }
    }

    private static async Task DeleteBestEffortAsync(INoteFileStore store, string key, ILogger logger)
    {
        try
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError("Could not delete an orphan export object: {ExceptionType}", e.GetType().Name);
        }
    }
}
