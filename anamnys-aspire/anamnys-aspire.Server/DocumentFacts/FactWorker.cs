using Anamnys.Server.Data;

namespace Anamnys.Server.DocumentFacts;

// Runs the queued extractions one at a time (the model runs under the shared LlmGate). A job is queued
// when a read saves its pages (ReadWorker) and by "Ler de novo". Logs carry the document id, counts and
// the exception type only: never page text or fact values.
public sealed class FactWorker(
    FactQueue queue, IServiceScopeFactory scopes, IFactExtractor extractor, TimeProvider clock, ILogger<FactWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SweepAsync(scopes, queue, logger, stoppingToken);
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                queue.Begin(job);
                try
                {
                    await RunAsync(job, scopes, extractor, clock, logger, stoppingToken);
                }
                finally
                {
                    queue.End(job);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Extractions started since the read after which the startup sweep gives up on a document: each one was
    // interrupted (it may be what brings the process down). Its pending facts stay; "Ler de novo" resets.
    public const int MaxSweepAttempts = 2;

    // At start: the queue is in memory, so extractions a previous process queued or was running are lost;
    // every document read without a facts_extracted entry since its read is queued again, unless its
    // extraction was already started MaxSweepAttempts times since that read.
    internal static async Task SweepAsync(IServiceScopeFactory scopes, FactQueue queue, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            var exhausted = await FactService.ExhaustedCountAsync(db, MaxSweepAttempts, ct);
            if (exhausted > 0)
            {
                logger.LogWarning("Left {Count} external documents out of fact extraction after {Attempts} interrupted attempts.",
                    exhausted, MaxSweepAttempts);
            }
            var waiting = await FactService.InterruptedAsync(db, MaxSweepAttempts, ct);
            foreach (var job in waiting)
            {
                queue.Enqueue(job);
            }
            if (waiting.Count > 0)
            {
                logger.LogInformation("Re-queued {Count} external documents for fact extraction.", waiting.Count);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError("External document fact extraction sweep failed: {ExceptionType}", e.GetType().Name);
        }
    }

    // Returns how many facts were saved for review; null when the document was gone or the run failed.
    internal static async Task<int?> RunAsync(
        FactJob job, IServiceScopeFactory scopes, IFactExtractor extractor, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            var pages = await FactService.PagesForExtractionAsync(db, job.ProviderId, job.DocumentId, ct);
            if (pages is null)
            {
                return null;
            }
            // The attempt is counted before the model runs, so an extraction that brings the process down
            // still counts and the startup sweep stops re-queuing it (MaxSweepAttempts).
            await FactService.CountExtractionAttemptAsync(db, job.ProviderId, job.DocumentId, ct);
            var started = clock.GetTimestamp();
            var candidates = await extractor.ExtractAsync(pages, ct);
            var saved = await FactService.SaveExtractedAsync(db, job.ProviderId, job.DocumentId, candidates, clock.GetUtcNow(), ct);
            if (saved is { } count)
            {
                logger.LogInformation("Extracted facts of external document {DocumentId}: {Saved} of {Candidates} kept, {Seconds:F1} s.",
                    job.DocumentId, count, candidates.Count, clock.GetElapsedTime(started).TotalSeconds);
            }
            return saved;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError("Fact extraction failed for external document {DocumentId}: {ExceptionType}", job.DocumentId, e.GetType().Name);
            return null;
        }
    }
}
