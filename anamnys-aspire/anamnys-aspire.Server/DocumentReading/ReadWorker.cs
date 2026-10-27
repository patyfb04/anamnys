using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.DocumentFacts;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.DocumentReading;

public static class DocumentReadStatus
{
    public const string Pending = "pending";
    public const string Reading = "reading";
    public const string Done = "done";
    public const string Failed = "failed";
}

public enum ReadStep { Done, Retry }

// One read at a time (the OCR is CPU-bound). A document is read once its scan turns clean; the pages
// replace any earlier read in one transaction. Failures are logged by exception type only: the content
// is patient data. At start it re-queues every pending read and every read a previous process left
// half-done (the queue is in memory).
public sealed class ReadWorker(
    ReadQueue queue, IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReadWorker> logger) : BackgroundService
{
    public const int MaxAttempts = 3;

    internal static TimeSpan BackoffAfter(int attempts) => attempts <= 1 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(scopes, queue, logger, stoppingToken, clock);
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var step = await ProcessAsync(job, scopes, clock, logger, stoppingToken);
            if (step.Step == ReadStep.Retry)
            {
                _ = RetryLaterAsync(job, step.Delay, stoppingToken);
            }
        }
    }

    private async Task RetryLaterAsync(ReadJob job, TimeSpan delay, CancellationToken ct)
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

    internal static async Task SweepAsync(
        IServiceScopeFactory scopes, ReadQueue queue, ILogger logger, CancellationToken ct, TimeProvider? clock = null)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            // A read interrupted on its last attempt (the process stopped or crashed during it) is not retried.
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var exhausted = await db.ExternalDocuments
                .Where(d => d.ReadStatus == DocumentReadStatus.Reading && d.ReadAttempts >= MaxAttempts
                    && d.ScanStatus == ExternalDocumentService.Clean && d.DeletedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.ReadStatus, DocumentReadStatus.Failed)
                    .SetProperty(d => d.ReadAt, now), ct);
            if (exhausted > 0)
            {
                logger.LogWarning("Marked {Count} interrupted external document reads failed after {Attempts} attempts.", exhausted, MaxAttempts);
            }
            var waiting = await db.ExternalDocuments.AsNoTracking()
                .Where(d => (d.ReadStatus == DocumentReadStatus.Pending || d.ReadStatus == DocumentReadStatus.Reading)
                    && d.ScanStatus == ExternalDocumentService.Clean && d.DeletedAt == null)
                .OrderBy(d => d.ScannedAt).ThenBy(d => d.UploadedAt)
                .Select(d => new ReadJob(d.Id, d.ProviderId))
                .ToListAsync(ct);
            foreach (var job in waiting)
            {
                queue.Enqueue(job);
            }
            if (waiting.Count > 0)
            {
                logger.LogInformation("Re-queued {Count} external documents for reading.", waiting.Count);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError("External document read sweep failed: {ExceptionType}", e.GetType().Name);
        }
    }

    // One attempt for one document. Retry carries the delay before the next attempt.
    internal static async Task<(ReadStep Step, TimeSpan Delay)> ProcessAsync(
        ReadJob job, IServiceScopeFactory scopes, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            var store = scope.ServiceProvider.GetRequiredService<INoteFileStore>();
            var reader = scope.ServiceProvider.GetRequiredService<IDocumentReader>();

            var document = await Waiting(db, job)
                .Select(d => new { d.ObjectKey, d.MimeType, d.ReadAttempts })
                .SingleOrDefaultAsync(ct);
            if (document is null)
            {
                return (ReadStep.Done, TimeSpan.Zero);
            }
            // A document whose attempts are used up was interrupted every time (it may be what brings the
            // process down): it is not read again.
            if (document.ReadAttempts >= MaxAttempts)
            {
                logger.LogError("External document {DocumentId} used up its read attempts; marking its read failed.", job.DocumentId);
                await MarkFailedAsync(db, job, clock.GetUtcNow(), ct);
                return (ReadStep.Done, TimeSpan.Zero);
            }
            // The attempt is counted before the read starts, so a crash during it still counts.
            var started = await Waiting(db, job)
                .Where(d => d.ReadAttempts == document.ReadAttempts)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.ReadStatus, DocumentReadStatus.Reading)
                    .SetProperty(d => d.ReadAttempts, d => d.ReadAttempts + 1), ct);
            if (started == 0)
            {
                return (ReadStep.Done, TimeSpan.Zero);
            }

            var content = document.ObjectKey is null ? null : await store.GetAsync(document.ObjectKey, ct);
            if (content is null)
            {
                logger.LogError("External document {DocumentId} has no stored object; marking its read failed.", job.DocumentId);
                await MarkFailedAsync(db, job, clock.GetUtcNow(), ct);
                return (ReadStep.Done, TimeSpan.Zero);
            }

            DocumentReadResult result;
            try
            {
                result = await reader.ReadAsync(content, document.MimeType, ct);
            }
            catch (Exception e) when (e is UnsupportedDocumentException or UnreadableDocumentException)
            {
                // Permanent: another attempt would read the same bytes the same way.
                logger.LogError("External document {DocumentId} cannot be read ({ExceptionType}); marking its read failed.",
                    job.DocumentId, e.InnerException?.GetType().Name ?? e.GetType().Name);
                await MarkFailedAsync(db, job, clock.GetUtcNow(), ct);
                return (ReadStep.Done, TimeSpan.Zero);
            }

            if (await SaveAsync(db, job, result, clock.GetUtcNow(), ct))
            {
                logger.LogInformation("Read external document {DocumentId}: {Pages} of {PageCount} pages, {OcrPages} by OCR.",
                    job.DocumentId, result.Pages.Count, result.PageCount, result.Pages.Count(p => p.FromOcr));
                // F-08: the facts are extracted from the pages just saved (optional, so the read runs alone in tests).
                scope.ServiceProvider.GetService<FactQueue>()?.Enqueue(new FactJob(job.DocumentId, job.ProviderId));
            }
            return (ReadStep.Done, TimeSpan.Zero);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Reading external document {DocumentId} failed: {ExceptionType}", job.DocumentId, e.GetType().Name);
            return await CountFailureAsync(job, scopes, clock, logger, ct);
        }
    }

    private static IQueryable<ExternalDocument> Waiting(AnamnysDbContext db, ReadJob job) =>
        db.ExternalDocuments.Where(d => d.Id == job.DocumentId && d.ProviderId == job.ProviderId
            && d.ScanStatus == ExternalDocumentService.Clean && d.DeletedAt == null
            && (d.ReadStatus == DocumentReadStatus.Pending || d.ReadStatus == DocumentReadStatus.Reading));

    // Replaces the pages of an earlier read; false when the document was deleted or is no longer being read.
    private static async Task<bool> SaveAsync(AnamnysDbContext db, ReadJob job, DocumentReadResult result, DateTimeOffset now, CancellationToken ct)
    {
        var saved = false;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            saved = false;
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Lock the row: a deletion running now either finished first (and is seen here) or waits for
            // this transaction and then removes the pages it wrote.
            var locked = await db.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM "ExternalDocuments"
                 WHERE "Id" = {job.DocumentId} AND "ProviderId" = {job.ProviderId}
                   AND "ReadStatus" = {DocumentReadStatus.Reading} AND "DeletedAt" IS NULL
                   FOR UPDATE
                """).ToListAsync(ct);
            if (locked.Count == 0)
            {
                return;
            }
            var updated = await db.ExternalDocuments
                .Where(d => d.Id == job.DocumentId && d.ProviderId == job.ProviderId
                    && d.ReadStatus == DocumentReadStatus.Reading && d.DeletedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.ReadStatus, DocumentReadStatus.Done)
                    .SetProperty(d => d.ReadAt, now)
                    .SetProperty(d => d.PageCount, result.PageCount)
                    .SetProperty(d => d.PagesTruncated, result.Truncated)
                    // A new read: the extraction attempts count from it (FactWorker.MaxSweepAttempts).
                    .SetProperty(d => d.ExtractAttempts, 0), ct);
            if (updated == 0)
            {
                return;
            }
            await db.ExternalDocumentPages
                .Where(p => p.ExternalDocumentId == job.DocumentId && p.ProviderId == job.ProviderId)
                .ExecuteDeleteAsync(ct);
            db.ExternalDocumentPages.AddRange(result.Pages.Select(p => new ExternalDocumentPage
            {
                Id = Guid.NewGuid(),
                ExternalDocumentId = job.DocumentId,
                ProviderId = job.ProviderId,
                PageNumber = p.PageNumber,
                Text = p.Text,
                FromOcr = p.FromOcr,
                CreatedAt = now,
            }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            saved = true;
        });
        return saved;
    }

    private static async Task<(ReadStep, TimeSpan)> CountFailureAsync(
        ReadJob job, IServiceScopeFactory scopes, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            // The attempt was already counted when the read started.
            var attempts = await Waiting(db, job).AsNoTracking()
                .Select(d => (int?)d.ReadAttempts)
                .SingleOrDefaultAsync(ct);
            if (attempts is not { } counted)
            {
                return (ReadStep.Done, TimeSpan.Zero);
            }
            if (counted >= MaxAttempts)
            {
                await MarkFailedAsync(db, job, clock.GetUtcNow(), ct);
                return (ReadStep.Done, TimeSpan.Zero);
            }
            await Waiting(db, job).ExecuteUpdateAsync(s => s.SetProperty(d => d.ReadStatus, DocumentReadStatus.Pending), ct);
            return (ReadStep.Retry, BackoffAfter(counted));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Could not record the failed read of external document {DocumentId}: {ExceptionType}", job.DocumentId, e.GetType().Name);
            return (ReadStep.Done, TimeSpan.Zero);
        }
    }

    private static Task<int> MarkFailedAsync(AnamnysDbContext db, ReadJob job, DateTimeOffset now, CancellationToken ct) =>
        Waiting(db, job).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.ReadStatus, DocumentReadStatus.Failed)
            .SetProperty(d => d.ReadAt, now), ct);
}
