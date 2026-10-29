using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notes;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.LiveSession;

public enum BeginCheck { Ok, Invalid, NotFound, Archived }

public enum FinalizeStatus { Ok, Expired }

public sealed record FinalizeResult(FinalizeStatus Status, Guid? NoteId);

// Database side of a live recording (design/specs/2026-10-10-live-session-transcription-design.md §5).
public static class LiveRecordings
{
    public static async Task<BeginCheck> CheckBeginAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid? appointmentId, string format, string language, CancellationToken ct)
    {
        if (!LiveSessionRules.IsValid(format, language))
        {
            return BeginCheck.Invalid;
        }
        // A discarded record (F-06) is gone; an archived one takes no new notes.
        var patient = await db.Patients.AsNoTracking()
            .Where(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null)
            .Select(p => new { p.ArchivedAt })
            .SingleOrDefaultAsync(ct);
        if (patient is null)
        {
            return BeginCheck.NotFound;
        }
        if (patient.ArchivedAt is not null)
        {
            return BeginCheck.Archived;
        }
        if (appointmentId is { } id
            && !await db.Appointments.AnyAsync(a => a.Id == id && a.ProviderId == providerId && a.PatientId == patientId, ct))
        {
            return BeginCheck.NotFound;
        }
        return BeginCheck.Ok;
    }

    public static async Task<FinalizeResult> FinalizeAsync(
        AnamnysDbContext db, RecordingBuffers buffers, TranscriptionQueue queue,
        Guid providerId, Guid recordingId, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.Notes.AsNoTracking()
            .Where(n => n.RecordingId == recordingId && n.ProviderId == providerId)
            .Select(n => (Guid?)n.Id)
            .SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            return new FinalizeResult(FinalizeStatus.Ok, existing);
        }
        if (buffers.Take(recordingId, providerId) is not { } taken)
        {
            return new FinalizeResult(FinalizeStatus.Expired, null);
        }

        // From here the audio lives only in this call, so the caller's token (the hub's
        // ConnectionAborted) is deliberately not used: a dropped socket during CommitAsync could
        // leave a committed Processing note whose audio was destroyed and never enqueued.
        var write = CancellationToken.None;
        try
        {
            var meta = taken.Meta;
            var noteId = Guid.NewGuid();
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(write);
                var appointment = meta.AppointmentId is { } appointmentId
                    ? await NoteSessions.LockAppointmentAsync(db, meta.ProviderId, meta.PatientId, appointmentId, write)
                        ?? throw new InvalidOperationException("The recording's appointment does not exist.")
                    : null;
                var session = await NoteSessions.AttendAsync(db, meta.ProviderId, meta.PatientId, appointment, meta.StartedAt, now, now, write);
                db.Notes.Add(new Note
                {
                    Id = noteId,
                    ProviderId = providerId,
                    PatientId = meta.PatientId,
                    SessionId = session.Id,
                    Status = "Processing",
                    InputMode = "VoiceLive",
                    Format = meta.Format,
                    StructureStatus = "pending",
                    RecordingId = recordingId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await db.SaveChangesAsync(write);
                NoteAudit.Add(db, noteId, providerId, "note_created", null, null, $"VoiceLive,{meta.Format}", now);
                await db.SaveChangesAsync(write);
                await tx.CommitAsync(write);
            });
            queue.Enqueue(new TranscriptionJob(noteId, providerId, meta.Language, taken.Audio));
            return new FinalizeResult(FinalizeStatus.Ok, noteId);
        }
        catch
        {
            taken.Audio.Destroy();
            throw;
        }
    }

    public static async Task<int> FailOrphansAsync(AnamnysDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var transcriptions = await db.Notes
            .Where(n => n.Status == "Processing" && n.RecordingId != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, "Failed")
                .SetProperty(n => n.FailureReason, "server_restart")
                .SetProperty(n => n.StructureStatus, (string?)null)
                .SetProperty(n => n.UpdatedAt, now), ct);
        var structurings = await db.Notes
            .Where(n => n.StructureStatus == "pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.StructureStatus, "failed")
                .SetProperty(n => n.StructureFailure, "server_restart")
                .SetProperty(n => n.UpdatedAt, now), ct);
        return transcriptions + structurings;
    }
}
