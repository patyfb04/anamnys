using Anamnys.Server.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.LiveSession;

// One job at a time: Whisper is CPU-bound (spec §4.3).
public sealed class TranscriptionWorker(
    TranscriptionQueue queue, IServiceScopeFactory scopes, IHubContext<ProgressHub> progress,
    TimeProvider clock, ILogger<TranscriptionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            queue.MarkStarted(job.NoteId);
            await RunJobAsync(job, scopes, progress, clock, logger, stoppingToken);
        }
    }

    internal static async Task RunJobAsync(
        TranscriptionJob job, IServiceScopeFactory scopes, IHubContext<ProgressHub> progress,
        TimeProvider clock, ILogger logger, CancellationToken stoppingToken)
    {
        var group = progress.Clients.Group(ProgressHub.Group(job.NoteId));
        try
        {
            await group.SendAsync("NoteProgress", job.NoteId, "transcribing", 0, stoppingToken);
            using var scope = scopes.CreateScope();
            var outcome = await NoteTranscription.ProcessAsync(
                scope.ServiceProvider.GetRequiredService<AnamnysDbContext>(),
                scope.ServiceProvider.GetRequiredService<ITranscriber>(),
                job, clock, logger, stoppingToken);
            await (outcome switch
            {
                TranscriptionOutcome.Ready => group.SendAsync("NoteReady", job.NoteId, stoppingToken),
                TranscriptionOutcome.NoSpeech => group.SendAsync("NoteFailed", job.NoteId, "no_speech", stoppingToken),
                _ => group.SendAsync("NoteFailed", job.NoteId, "transcription_error", stoppingToken),
            });
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Transcription job for note {NoteId} failed: {ExceptionType}", job.NoteId, e.GetType().Name);
            await FailBestEffortAsync(job, scopes, group, clock, logger, stoppingToken);
        }
        finally
        {
            job.Audio.Destroy();
        }
    }

    // Never overwrites a note that already left Processing (e.g. Ready, when only the final notification failed).
    private static async Task FailBestEffortAsync(
        TranscriptionJob job, IServiceScopeFactory scopes, IClientProxy group,
        TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            var now = clock.GetUtcNow();
            var updated = await db.Notes
                .Where(n => n.Id == job.NoteId && n.ProviderId == job.ProviderId && n.Status == "Processing")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.Status, "Failed")
                    .SetProperty(n => n.FailureReason, "transcription_error")
                    .SetProperty(n => n.UpdatedAt, now), ct);
            if (updated > 0)
            {
                await group.SendAsync("NoteFailed", job.NoteId, "transcription_error", ct);
            }
        }
        catch (Exception e)
        {
            logger.LogError("Could not mark note {NoteId} as failed: {ExceptionType}", job.NoteId, e.GetType().Name);
        }
    }
}
