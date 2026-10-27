using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.DocumentReading;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.ExternalDocuments;

public enum ScanStep { Done, Retry }

// One scan at a time. Failures are logged by exception type only: the content is patient data.
// At start it re-queues every pending document (the queue is in memory) and finishes the removal of
// infected objects a previous process left behind.
public sealed class ScanWorker(
    ScanQueue queue, IServiceScopeFactory scopes, TimeProvider clock, ILogger<ScanWorker> logger) : BackgroundService
{
    public const int MaxAttempts = 3;

    internal static TimeSpan BackoffAfter(int attempts) => attempts <= 1 ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(60);

    private static readonly TimeSpan MaxUnavailableBackoff = TimeSpan.FromMinutes(5);
    // A document whose scan keeps losing the connection after clamd accepted it is probably the cause itself
    // (a file clamd chokes on); after this long it is marked failed instead of retrying forever.
    internal static readonly TimeSpan MaxConnectionLostDuration = TimeSpan.FromMinutes(30);

    private sealed record Unavailability(int Streak, DateTimeOffset? LostSince);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Unavailability> UnavailableStreaks = new();

    // 10 s, 20 s, 40 s ... capped at 5 min, for as long as the scanner stays unreachable.
    internal static TimeSpan UnavailableBackoff(int streak) =>
        TimeSpan.FromSeconds(Math.Min(MaxUnavailableBackoff.TotalSeconds, 10 * Math.Pow(2, Math.Min(streak - 1, 10))));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(scopes, queue, logger, stoppingToken);
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var step = await ProcessAsync(job, scopes, clock, logger, stoppingToken);
            if (step.Step == ScanStep.Retry)
            {
                _ = RetryLaterAsync(job, step.Delay, stoppingToken);
            }
        }
    }

    private async Task RetryLaterAsync(ScanJob job, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, clock, ct);
            queue.Enqueue(job);
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static async Task SweepAsync(IServiceScopeFactory scopes, ScanQueue queue, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<INoteFileStore>();

            var pending = await db.ExternalDocuments.AsNoTracking()
                .Where(d => d.ScanStatus == ExternalDocumentService.Pending && d.DeletedAt == null)
                .OrderBy(d => d.UploadedAt)
                .Select(d => new ScanJob(d.Id, d.ProviderId))
                .ToListAsync(ct);
            foreach (var job in pending)
            {
                queue.Enqueue(job);
            }
            if (pending.Count > 0)
            {
                logger.LogInformation("Re-queued {Count} external documents for scanning.", pending.Count);
            }

            var leftovers = await db.ExternalDocuments.AsNoTracking()
                .Where(d => d.ScanStatus == ExternalDocumentService.Infected && d.ObjectKey != null)
                .Select(d => new { d.Id, d.ProviderId })
                .ToListAsync(ct);
            foreach (var leftover in leftovers)
            {
                await RemoveInfectedObjectAsync(db, store, leftover.Id, leftover.ProviderId, logger, ct);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError("External document scan sweep failed: {ExceptionType}", e.GetType().Name);
        }
    }

    // One attempt for one document. Retry carries the delay before the next attempt.
    internal static async Task<(ScanStep Step, TimeSpan Delay)> ProcessAsync(
        ScanJob job, IServiceScopeFactory scopes, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var step = await ProcessCoreAsync(job, scopes, clock, logger, ct);
        if (step.Step == ScanStep.Done)
        {
            UnavailableStreaks.TryRemove(job.DocumentId, out _);
        }
        return step;
    }

    private static async Task<(ScanStep Step, TimeSpan Delay)> ProcessCoreAsync(
        ScanJob job, IServiceScopeFactory scopes, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<INoteFileStore>();
            var scanner = scope.ServiceProvider.GetRequiredService<IVirusScanner>();

            var document = await db.ExternalDocuments.AsNoTracking()
                .SingleOrDefaultAsync(d => d.Id == job.DocumentId && d.ProviderId == job.ProviderId
                    && d.ScanStatus == ExternalDocumentService.Pending && d.DeletedAt == null, ct);
            if (document is null)
            {
                return (ScanStep.Done, TimeSpan.Zero);
            }

            var content = document.ObjectKey is null ? null : await store.GetAsync(document.ObjectKey, ct);
            if (content is null)
            {
                logger.LogError("External document {DocumentId} has no stored object; marking it failed.", job.DocumentId);
                await MarkFailedAsync(db, job, clock.GetUtcNow(), document.ScanAttempts, ct);
                return (ScanStep.Done, TimeSpan.Zero);
            }

            ScanVerdict verdict;
            try
            {
                verdict = await scanner.ScanAsync(content, ct);
            }
            catch (ScannerUnavailableException e)
            {
                var now = clock.GetUtcNow();
                var state = UnavailableStreaks.AddOrUpdate(
                    job.DocumentId,
                    _ => new Unavailability(1, e.ConnectionLost ? now : null),
                    (_, old) => new Unavailability(old.Streak + 1, e.ConnectionLost ? old.LostSince ?? now : null));
                logger.LogWarning("Scanner unavailable for external document {DocumentId} (streak {Streak}): {ExceptionType}",
                    job.DocumentId, state.Streak, e.InnerException?.GetType().Name ?? e.GetType().Name);
                if (state.LostSince is { } since && now - since >= MaxConnectionLostDuration)
                {
                    logger.LogError("External document {DocumentId} kept losing the scanner connection for {Minutes} min; marking it failed.",
                        job.DocumentId, (int)(now - since).TotalMinutes);
                    await MarkFailedAsync(db, job, now, document.ScanAttempts, ct);
                    return (ScanStep.Done, TimeSpan.Zero);
                }
                return (ScanStep.Retry, UnavailableBackoff(state.Streak));
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                var attempts = document.ScanAttempts + 1;
                logger.LogError("Scanning external document {DocumentId} failed (attempt {Attempt} of {Attempts}): {ExceptionType}",
                    job.DocumentId, attempts, MaxAttempts, e.GetType().Name);
                if (attempts >= MaxAttempts)
                {
                    await MarkFailedAsync(db, job, clock.GetUtcNow(), attempts, ct);
                    return (ScanStep.Done, TimeSpan.Zero);
                }
                await db.ExternalDocuments
                    .Where(d => d.Id == job.DocumentId && d.ProviderId == job.ProviderId)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.ScanAttempts, attempts), ct);
                return (ScanStep.Retry, BackoffAfter(attempts));
            }

            if (verdict.Clean)
            {
                // A clean document is queued for reading (F-08); the read worker's sweep picks up the row
                // if the process stops before the job is taken.
                var cleaned = await db.ExternalDocuments
                    .Where(d => d.Id == job.DocumentId && d.ProviderId == job.ProviderId
                        && d.ScanStatus == ExternalDocumentService.Pending && d.DeletedAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.ScanStatus, ExternalDocumentService.Clean)
                        .SetProperty(d => d.ScannedAt, clock.GetUtcNow())
                        .SetProperty(d => d.ReadStatus, DocumentReadStatus.Pending)
                        .SetProperty(d => d.ReadAttempts, 0)
                        .SetProperty(d => d.ExtractAttempts, 0), ct);
                if (cleaned > 0)
                {
                    scope.ServiceProvider.GetService<ReadQueue>()?.Enqueue(new ReadJob(job.DocumentId, job.ProviderId));
                }
            }
            else if (await BlockAsync(db, document, clock.GetUtcNow(), ct))
            {
                await RemoveInfectedObjectAsync(db, store, job.DocumentId, job.ProviderId, logger, ct);
            }
            return (ScanStep.Done, TimeSpan.Zero);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Storage or database trouble outside the scanner: try again like a scanner error.
            logger.LogError("External document {DocumentId} could not be processed: {ExceptionType}", job.DocumentId, e.GetType().Name);
            return await CountFailureAsync(job, scopes, clock, logger, ct);
        }
    }

    private static async Task<(ScanStep, TimeSpan)> CountFailureAsync(
        ScanJob job, IServiceScopeFactory scopes, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            var attempts = await db.ExternalDocuments.AsNoTracking()
                .Where(d => d.Id == job.DocumentId && d.ProviderId == job.ProviderId && d.ScanStatus == ExternalDocumentService.Pending)
                .Select(d => (int?)d.ScanAttempts)
                .SingleOrDefaultAsync(ct);
            if (attempts is not { } before)
            {
                return (ScanStep.Done, TimeSpan.Zero);
            }
            var next = before + 1;
            if (next >= MaxAttempts)
            {
                await MarkFailedAsync(db, job, clock.GetUtcNow(), next, ct);
                return (ScanStep.Done, TimeSpan.Zero);
            }
            await db.ExternalDocuments
                .Where(d => d.Id == job.DocumentId && d.ProviderId == job.ProviderId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ScanAttempts, next), ct);
            return (ScanStep.Retry, BackoffAfter(next));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Could not record the failed scan of external document {DocumentId}: {ExceptionType}", job.DocumentId, e.GetType().Name);
            return (ScanStep.Done, TimeSpan.Zero);
        }
    }

    private static Task<int> MarkFailedAsync(AnamnysDbContext db, ScanJob job, DateTimeOffset now, int attempts, CancellationToken ct) =>
        db.ExternalDocuments
            .Where(d => d.Id == job.DocumentId && d.ProviderId == job.ProviderId && d.ScanStatus == ExternalDocumentService.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ScanStatus, ExternalDocumentService.Failed)
                .SetProperty(d => d.ScannedAt, now)
                .SetProperty(d => d.ScanAttempts, attempts), ct);

    // Marks the document infected and audits it in one transaction; false when it is no longer pending.
    private static async Task<bool> BlockAsync(AnamnysDbContext db, ExternalDocument document, DateTimeOffset now, CancellationToken ct)
    {
        var blocked = false;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            blocked = false;
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var updated = await db.ExternalDocuments
                .Where(d => d.Id == document.Id && d.ProviderId == document.ProviderId
                    && d.ScanStatus == ExternalDocumentService.Pending && d.DeletedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.ScanStatus, ExternalDocumentService.Infected)
                    .SetProperty(d => d.ScannedAt, now), ct);
            if (updated == 0)
            {
                return;
            }
            ExternalDocumentService.AddAudit(db, document.ProviderId, ExternalDocumentService.BlockedAction, document, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            blocked = true;
        });
        return blocked;
    }

    // The key stays on the row until the object is gone, so a failed delete is retried by the next sweep.
    private static async Task RemoveInfectedObjectAsync(
        AnamnysDbContext db, INoteFileStore store, Guid documentId, Guid providerId, ILogger logger, CancellationToken ct)
    {
        try
        {
            var key = ExternalDocumentService.ObjectKeyFor(providerId, documentId);
            await store.DeleteAsync(key, ct);
            await db.ExternalDocuments
                .Where(d => d.Id == documentId && d.ProviderId == providerId && d.ScanStatus == ExternalDocumentService.Infected)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ObjectKey, (string?)null), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Could not delete the object of blocked external document {DocumentId}: {ExceptionType}", documentId, e.GetType().Name);
        }
    }
}
