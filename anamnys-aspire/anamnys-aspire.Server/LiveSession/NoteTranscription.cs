using Anamnys.Server.Data;
using Anamnys.Server.Pseudonymization;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.LiveSession;

// Gone: the note left Processing during the pseudonymisation step (deleted or discarded); nothing to notify.
public enum TranscriptionOutcome { Ready, NoSpeech, Error, PseudonymizationFailed, Gone }

// One queued recording: transcribe, pseudonymise, write the note, and destroy the audio on every path
// (design/specs/2026-10-10-live-session-transcription-design.md §4.3 and §7; F-27: the transcript goes through the
// pseudonymisation step before it is written, design/specs/2026-11-17-pseudonymization-design.md §2).
// Delivery 2 adds the structuring step between the transcript and ReadyForReview.
public static class NoteTranscription
{
    public static async Task<TranscriptionOutcome> ProcessAsync(
        AnamnysDbContext db, ITranscriber transcriber, PseudonymizationStep pseudonymization, TranscriptionJob job, TimeProvider clock,
        ILogger logger, CancellationToken ct)
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
            Array.Clear(samples);
            job.Audio.Destroy();
            // The record first: the note page stops polling once the note is Failed, and must then show the destruction.
            await RecordDestructionAsync(db, job, AudioDestruction.Failed, clock, logger, ct);
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
            await RecordDestructionAsync(db, job, AudioDestruction.Failed, clock, logger, ct);
            await FailAsync(db, job.NoteId, "no_speech", clock.GetUtcNow(), ct);
            return TranscriptionOutcome.NoSpeech;
        }
        // The audio was transcribed whatever the step's outcome; the original text lives only in this call (and, on a
        // failure, in the step's in-memory hold).
        var outcome = await pseudonymization.RunAsync(db, job.NoteId, job.ProviderId, job.Meta.PatientId, text, ct);
        db.ChangeTracker.Clear();
        await RecordDestructionAsync(db, job, AudioDestruction.Transcribed, clock, logger, ct);
        return outcome switch
        {
            PseudonymizationOutcome.Done => TranscriptionOutcome.Ready,
            PseudonymizationOutcome.Failed => TranscriptionOutcome.PseudonymizationFailed,
            _ => TranscriptionOutcome.Gone,
        };
    }

    // F-26: the audio is already zeroed here; the record never throws but on shutdown.
    private static Task RecordDestructionAsync(
        AnamnysDbContext db, TranscriptionJob job, string outcome, TimeProvider clock, ILogger logger, CancellationToken ct) =>
        AudioDestruction.TryRecordAsync(db, job.RecordingId, job.Meta, job.NoteId, outcome, clock.GetUtcNow(), logger, ct);

    private static Task<int> FailAsync(AnamnysDbContext db, Guid noteId, string reason, DateTimeOffset now, CancellationToken ct) =>
        db.Notes
            .Where(n => n.Id == noteId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, "Failed")
                .SetProperty(n => n.FailureReason, reason)
                .SetProperty(n => n.StructureStatus, (string?)null)
                .SetProperty(n => n.UpdatedAt, now), ct);
}
