using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.LiveSession;

public enum BeginCheck { Ok, Invalid, NotFound }

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
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.ProviderId == providerId, ct))
        {
            return BeginCheck.NotFound;
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
                var session = await SessionForAsync(db, meta, now, write);
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

    private static async Task<Session> SessionForAsync(AnamnysDbContext db, RecordingMeta meta, DateTimeOffset now, CancellationToken ct)
    {
        var appointment = meta.AppointmentId is { } id
            ? await db.Appointments.SingleAsync(a => a.Id == id && a.ProviderId == meta.ProviderId, ct)
            : null;
        var session = appointment?.SessionId is { } sessionId
            ? await db.Sessions.SingleAsync(s => s.Id == sessionId, ct)
            : null;
        if (session is null)
        {
            session = new Session
            {
                Id = Guid.NewGuid(),
                PatientId = meta.PatientId,
                ProviderId = meta.ProviderId,
                ScheduledStart = appointment?.StartsAt,
                ScheduledMinutes = appointment is null ? null : (int)(appointment.EndsAt - appointment.StartsAt).TotalMinutes,
                Modality = appointment?.Modality is "presencial" or "online" ? appointment.Modality : null,
                ActualStart = meta.StartedAt,
                CreatedAt = now,
            };
            db.Sessions.Add(session);
            // No navigations are mapped, so EF cannot order inserts by foreign key: persist the session first.
            await db.SaveChangesAsync(ct);
            if (appointment is not null)
            {
                appointment.SessionId = session.Id;
            }
        }
        session.ActualStart ??= meta.StartedAt;
        session.ActualEnd = now;
        session.Attendance = "attended";
        return session;
    }
}
