using Anamnys.Server.Data;
using Anamnys.Server.NoteStructuring;
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
            switch (job)
            {
                case TranscriptionJob transcription:
                    await RunJobAsync(transcription, scopes, progress, clock, logger, stoppingToken);
                    break;
                case StructureJob structure:
                    await RunStructureAsync(structure.NoteId, structure.ProviderId, scopes, progress, clock, logger, stoppingToken, structure.Format);
                    break;
            }
        }
    }

    internal static async Task RunJobAsync(
        TranscriptionJob job, IServiceScopeFactory scopes, IHubContext<ProgressHub> progress,
        TimeProvider clock, ILogger logger, CancellationToken stoppingToken)
    {
        var group = progress.Clients.Group(ProgressHub.Group(job.NoteId));
        var ready = false;
        try
        {
            await group.SendAsync("NoteProgress", job.NoteId, "transcribing", 0, stoppingToken);
            using var scope = scopes.CreateScope();
            var outcome = await NoteTranscription.ProcessAsync(
                scope.ServiceProvider.GetRequiredService<AnamnysDbContext>(),
                scope.ServiceProvider.GetRequiredService<ITranscriber>(),
                job, clock, logger, stoppingToken);
            ready = outcome == TranscriptionOutcome.Ready;
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

        // The audio is already destroyed: structuring reads only the stored transcript.
        if (ready)
        {
            await RunStructureAsync(job.NoteId, job.ProviderId, scopes, progress, clock, logger, stoppingToken);
        }
    }

    // Never throws: a structuring failure leaves the transcript as the note (spec §5).
    internal static async Task RunStructureAsync(
        Guid noteId, Guid providerId, IServiceScopeFactory scopes, IHubContext<ProgressHub> progress,
        TimeProvider clock, ILogger logger, CancellationToken ct, string? format = null)
    {
        var group = progress.Clients.Group(ProgressHub.Group(noteId));
        try
        {
            await group.SendAsync("NoteProgress", noteId, "structuring", 0, ct);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            var outcome = await NoteStructuringJob.ProcessAsync(
                db, scope.ServiceProvider.GetRequiredService<INoteStructurer>(), noteId, providerId, clock, logger, ct, format);
            switch (outcome)
            {
                case StructuringOutcome.Done or StructuringOutcome.Discarded:
                    await group.SendAsync("NoteStructured", noteId, ct);
                    break;
                case StructuringOutcome.Failed:
                    var reason = await db.Notes.AsNoTracking()
                        .Where(n => n.Id == noteId && n.ProviderId == providerId)
                        .Select(n => n.StructureFailure)
                        .SingleOrDefaultAsync(ct);
                    await group.SendAsync("NoteStructureFailed", noteId, reason ?? "model_error", ct);
                    break;
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Structuring job for note {NoteId} failed: {ExceptionType}", noteId, e.GetType().Name);
            await StructureFailBestEffortAsync(noteId, providerId, scopes, group, clock, logger, ct);
        }
    }

    // Only a note still pending is marked failed.
    private static async Task StructureFailBestEffortAsync(
        Guid noteId, Guid providerId, IServiceScopeFactory scopes, IClientProxy group,
        TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            var now = clock.GetUtcNow();
            var updated = await db.Notes
                .Where(n => n.Id == noteId && n.ProviderId == providerId && n.StructureStatus == "pending")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.StructureStatus, "failed")
                    .SetProperty(n => n.StructureFailure, "model_error")
                    .SetProperty(n => n.UpdatedAt, now), ct);
            if (updated > 0)
            {
                await group.SendAsync("NoteStructureFailed", noteId, "model_error", ct);
            }
        }
        catch (Exception e)
        {
            logger.LogError("Could not mark structuring of note {NoteId} as failed: {ExceptionType}", noteId, e.GetType().Name);
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
                    .SetProperty(n => n.StructureStatus, (string?)null)
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
