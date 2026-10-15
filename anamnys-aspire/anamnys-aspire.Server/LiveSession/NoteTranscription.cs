using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.LiveSession;

public enum TranscriptionOutcome { Ready, NoSpeech, Error }

// One queued recording: transcribe, write the note, and destroy the audio on every path
// (design/specs/2026-10-10-live-session-transcription-design.md §4.3 and §7).
// Delivery 2 adds the structuring step between the transcript and ReadyForReview.
public static class NoteTranscription
{
    public static async Task<TranscriptionOutcome> ProcessAsync(
        AnamnysDbContext db, ITranscriber transcriber, TranscriptionJob job, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var samples = job.Audio.ToSamples();
        string text;
        try
        {
            text = (await transcriber.TranscribeAsync(samples, job.Language, ct)).Trim();
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Exception type only: messages from native code could echo input.
            logger.LogError("Transcription failed for note {NoteId}: {ExceptionType}", job.NoteId, e.GetType().Name);
            await FailAsync(db, job.NoteId, "transcription_error", clock.GetUtcNow(), ct);
            return TranscriptionOutcome.Error;
        }
        finally
        {
            Array.Clear(samples);
            job.Audio.Destroy();
        }

        if (text.Length == 0)
        {
            await FailAsync(db, job.NoteId, "no_speech", clock.GetUtcNow(), ct);
            return TranscriptionOutcome.NoSpeech;
        }
        await db.Notes
            .Where(n => n.Id == job.NoteId && n.ProviderId == job.ProviderId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.RawTranscript, text)
                .SetProperty(n => n.Status, "ReadyForReview")
                .SetProperty(n => n.UpdatedAt, clock.GetUtcNow()), ct);
        return TranscriptionOutcome.Ready;
    }

    private static Task<int> FailAsync(AnamnysDbContext db, Guid noteId, string reason, DateTimeOffset now, CancellationToken ct) =>
        db.Notes
            .Where(n => n.Id == noteId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, "Failed")
                .SetProperty(n => n.FailureReason, reason)
                .SetProperty(n => n.StructureStatus, (string?)null)
                .SetProperty(n => n.UpdatedAt, now), ct);
}
