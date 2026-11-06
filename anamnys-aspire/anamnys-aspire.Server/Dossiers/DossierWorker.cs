using System.Security.Cryptography;
using Anamnys.Server.Data;
using Anamnys.Server.Exports;
using Anamnys.Server.Integrity;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Dossiers;

// One dossier at a time (design/specs/2026-11-04-patient-dossier-design.md §3): build the ZIP to a temporary file, hash
// it, timestamp the hash, upload, complete. Failures are logged by exception type only: the build handles patient data.
public sealed class DossierWorker(
    DossierQueue queue, IServiceScopeFactory scopes, TimeProvider clock, ILogger<DossierWorker> logger) : BackgroundService
{
    public const string TempFilePattern = "anamnys-dossier-*.zip";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ExportWorker.DeleteLeftoverTempFiles(Path.GetTempPath(), clock.GetUtcNow(), logger, TempFilePattern);
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await RunAsync(job, scopes, clock, logger, stoppingToken);
        }
    }

    internal static async Task RunAsync(
        DossierJob job, IServiceScopeFactory scopes, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        string? objectKey = null;
        INoteFileStore? store = null;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            store = scope.ServiceProvider.GetRequiredService<INoteFileStore>();
            var tsa = scope.ServiceProvider.GetRequiredService<ITimestampAuthority>();

            var started = await db.Dossiers
                .Where(x => x.Id == job.DossierId && x.ProviderId == job.ProviderId && x.Status == DossierService.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DossierService.Running), ct);
            if (started == 0)
            {
                return;
            }
            var row = await db.Dossiers.AsNoTracking()
                .Where(x => x.Id == job.DossierId && x.ProviderId == job.ProviderId)
                .Select(x => new { x.PatientId, x.Number, x.Reason, x.ReasonNote, x.PeriodFrom, x.PeriodTo })
                .SingleAsync(ct);
            var generatedAt = clock.GetUtcNow();
            var request = new DossierRequest(
                job.DossierId, row.Number, job.ProviderId, row.PatientId, row.Reason, row.ReasonNote, row.PeriodFrom, row.PeriodTo,
                generatedAt);

            await using var file = new FileStream(
                Path.Combine(Path.GetTempPath(), $"anamnys-dossier-{Guid.NewGuid():N}.zip"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            var built = await DossierBuilder.BuildAsync(db, store, tsa, request, file, ct);
            await file.FlushAsync(ct);
            var size = file.Length;
            file.Position = 0;
            var digest = await SHA256.HashDataAsync(file, ct);
            var zipSha256 = Convert.ToHexStringLower(digest);
            var (token, anchoredAt) = await StampAsync(tsa, digest, job, logger, ct);

            file.Position = 0;
            objectKey = DossierService.ObjectKeyFor(job.ProviderId, job.DossierId);
            await store.PutAsync(objectKey, file, "application/zip", ct);
            var completion = new DossierCompletion(
                objectKey, size, zipSha256, built.Manifest, token, anchoredAt, built.IntegrityVerified, generatedAt);
            if (!await DossierService.CompleteAsync(db, job, completion, clock.GetUtcNow(), ct))
            {
                await DeleteBestEffortAsync(store, objectKey, logger);
            }
        }
        catch (Exception e)
        {
            if (store is not null && objectKey is not null)
            {
                await DeleteBestEffortAsync(store, objectKey, logger);
            }
            if (ct.IsCancellationRequested)
            {
                // Shutting down: the row stays running and the next start marks it interrupted.
                return;
            }
            logger.LogError("Dossier {DossierId} failed: {ExceptionType}", job.DossierId, e.GetType().Name);
            await FailBestEffortAsync(job, scopes, logger);
        }
    }

    // The ZIP's own SHA-256 digest stamped by the F-45 authority, so `openssl ts -verify -data <zip>` checks the file the
    // recipient holds. An outage does not fail the dossier: it goes out without a token, and the delivery receipt says so.
    private static async Task<(byte[]? Token, DateTimeOffset? AnchoredAt)> StampAsync(
        ITimestampAuthority tsa, byte[] digest, DossierJob job, ILogger logger, CancellationToken ct)
    {
        try
        {
            var token = await tsa.StampDigestAsync(digest, ct);
            if (tsa.VerifyDigest(token, digest) is { } genTime)
            {
                return (token, genTime);
            }
            logger.LogWarning("Dossier {DossierId} goes out without a timestamp: the token did not verify.", job.DossierId);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Dossier {DossierId} goes out without a timestamp: {ExceptionType}", job.DossierId, e.GetType().Name);
        }
        return (null, null);
    }

    private static async Task FailBestEffortAsync(DossierJob job, IServiceScopeFactory scopes, ILogger logger)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            await DossierService.FailAsync(db, job, DossierService.DossierError, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError("Could not mark dossier {DossierId} as failed: {ExceptionType}", job.DossierId, e.GetType().Name);
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
            logger.LogError("Could not delete an orphan dossier object: {ExceptionType}", e.GetType().Name);
        }
    }
}

// At start: dossiers left running by a previous process are failed (interrupted) and the ones still queued are enqueued
// again. Then daily: ZIPs past their lifetime are deleted and their rows marked expired (the rows stay).
public sealed class DossierSweep(
    DossierQueue queue, IServiceScopeFactory scopes, TimeProvider clock, ILogger<DossierSweep> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private readonly DateTimeOffset startedAt = clock.GetUtcNow();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(atStart: true, stoppingToken);
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SweepAsync(atStart: false, stoppingToken);
        }
    }

    private async Task SweepAsync(bool atStart, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            if (atStart)
            {
                await RecoverAsync(db, queue, startedAt, logger, ct);
            }
            var expired = await DossierService.ExpireAsync(
                db, scope.ServiceProvider.GetRequiredService<INoteFileStore>(), clock.GetUtcNow(), logger, ct);
            if (expired > 0)
            {
                logger.LogInformation("Expired {Count} dossier files.", expired);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Dossier sweep failed: {ExceptionType}", e.GetType().Name);
        }
    }

    internal static async Task RecoverAsync(
        AnamnysDbContext db, DossierQueue queue, DateTimeOffset startedAt, ILogger logger, CancellationToken ct)
    {
        var failed = await DossierService.FailInterruptedAsync(db, startedAt, ct);
        if (failed > 0)
        {
            logger.LogWarning("Marked {Count} interrupted dossiers as failed.", failed);
        }
        var queued = await DossierService.QueuedBeforeAsync(db, startedAt, ct);
        foreach (var job in queued)
        {
            queue.Enqueue(job);
        }
        if (queued.Count > 0)
        {
            logger.LogInformation("Enqueued {Count} dossiers again after a restart.", queued.Count);
        }
    }
}
