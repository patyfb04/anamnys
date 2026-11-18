using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.LiveSession;

// The record that a recording's audio was destroyed (design/specs/2026-11-16-transient-audio-tab-capture-design.md §2.2):
// one AudioDestructionLogs row and an audio_destroyed audit entry, written right after the audio is zeroed. Ids, codes
// and the outcome only.
public static class AudioDestruction
{
    public const string Transcribed = "transcribed";
    public const string Failed = "failed";
    public const string Abandoned = "abandoned";
    public const string AuditAction = "audio_destroyed";
    public const string AuditField = "recording";

    public static async Task RecordAsync(
        AnamnysDbContext db, Guid recordingId, RecordingMeta meta, Guid? noteId, string outcome, DateTimeOffset now, CancellationToken ct)
    {
        var log = new AudioDestructionLog
        {
            Id = Guid.NewGuid(),
            SessionId = recordingId,
            ProviderId = meta.ProviderId,
            PatientId = meta.PatientId,
            NoteId = noteId,
            RecordingMode = meta.Mode,
            Sources = [.. meta.Sources],
            DestroyedAt = now,
            Outcome = outcome,
        };
        // Not tied to the note: it is the recording's trail, not a note mutation.
        var audit = new AuditEntry
        {
            Id = Guid.NewGuid(),
            ActorId = meta.ProviderId,
            Action = AuditAction,
            FieldChanged = AuditField,
            NewValue = $"{recordingId} {(noteId?.ToString() ?? "-")} {outcome}",
            At = now,
        };
        db.AudioDestructionLogs.Add(log);
        db.AuditEntries.Add(audit);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // A failed write must not linger in the context and be retried by a later save.
            db.Entry(log).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            db.Entry(audit).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            throw;
        }
    }

    // Never throws but on shutdown: a failure is logged (recording id and exception type only) and never keeps audio
    // alive, since the audio is already zeroed.
    public static async Task<bool> TryRecordAsync(
        AnamnysDbContext db, Guid recordingId, RecordingMeta meta, Guid? noteId, string outcome, DateTimeOffset now,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            await RecordAsync(db, recordingId, meta, noteId, outcome, now, ct);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError("Audio destruction record failed for recording {RecordingId}: {ExceptionType}", recordingId, e.GetType().Name);
            return false;
        }
    }
}
