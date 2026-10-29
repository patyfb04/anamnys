using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Retention;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

// The appointment → session → note chain, shared by typed notes and the live finalize
// (design/specs/2026-10-15-note-editor-design.md §4.3). Callers own the transaction.
public static class NoteSessions
{
    // Locks first, so two notes for one appointment serialise; then loads it tracked.
    public static async Task<Appointment?> LockAppointmentAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid appointmentId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM \"Appointments\" WHERE \"Id\" = {appointmentId} AND \"ProviderId\" = {providerId} FOR UPDATE", ct);
        return await db.Appointments.SingleOrDefaultAsync(
            a => a.Id == appointmentId && a.ProviderId == providerId && a.PatientId == patientId, ct);
    }

    public static async Task<bool> IsAvailableForNoteAsync(AnamnysDbContext db, Appointment appointment, CancellationToken ct) =>
        appointment.Status != AppointmentStatus.Cancelled
        && !(appointment.SessionId is { } sessionId && await db.Notes.AnyAsync(n => n.SessionId == sessionId, ct));

    public static async Task<Session> AttendAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Appointment? appointment,
        DateTimeOffset? actualStart, DateTimeOffset? actualEnd, DateTimeOffset now, CancellationToken ct)
    {
        var session = appointment?.SessionId is { } sessionId
            ? await db.Sessions.SingleAsync(s => s.Id == sessionId, ct)
            : null;
        if (session is null)
        {
            session = new Session
            {
                Id = Guid.NewGuid(),
                PatientId = patientId,
                ProviderId = providerId,
                ScheduledStart = appointment?.StartsAt,
                ScheduledMinutes = appointment is null ? null : (int)(appointment.EndsAt - appointment.StartsAt).TotalMinutes,
                Modality = appointment?.Modality is "presencial" or "online" ? appointment.Modality : null,
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
        if (actualStart is not null)
        {
            session.ActualStart ??= actualStart;
        }
        if (actualEnd is not null)
        {
            session.ActualEnd = actualEnd;
        }
        session.Attendance = "attended";
        // A session is a record of the patient: the retention deadline moves with it (F-06). The push is an UPDATE
        // dated by the session, independent of the tracked changes the caller saves later; it relies on the
        // caller's transaction (both callers own one) so the deadline commits or rolls back with the session.
        await RetentionClock.PushAsync(db, providerId, patientId, session.ActualStart ?? session.CreatedAt, ct);

        var nowUtc = now.ToUniversalTime();
        if (appointment is { Status: AppointmentStatus.Scheduled or AppointmentStatus.Confirmed } && appointment.StartsAt <= nowUtc)
        {
            await Appointments.Appointments.ApplyAttendedAsync(db, appointment, nowUtc, ct);
        }
        return session;
    }
}
