using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Attendance;

// Loads one patient's attendance events up to now (design/specs/2026-10-27-attendance-stats-design.md §2, §3).
// The appointment is the base; a session counts on its own only when no appointment points to it and it was
// recorded as attended (a "scheduled" session with no appointment is only a plan, not an attendance).
// Dates only; no PHI leaves here.
public static class AttendanceQuery
{
    public static async Task<IReadOnlyList<AttendanceEvent>?> EventsAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateOnly? from, DateOnly? to, DateTimeOffset now, CancellationToken ct)
    {
        if (!await db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null, ct))
        {
            return null;
        }

        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => a.ProviderId == providerId && a.PatientId == patientId && a.StartsAt <= now)
            .Select(a => new { a.Id, a.StartsAt, a.Status, a.CancelledAt, a.CancelledBy, a.SessionId })
            .ToListAsync(ct);
        var linkedIds = appointments.Where(a => a.SessionId != null).Select(a => a.SessionId!.Value).ToList();
        var attendedLinked = (await db.Sessions.AsNoTracking()
            .Where(s => linkedIds.Contains(s.Id) && s.Attendance == "attended")
            .Select(s => s.Id)
            .ToListAsync(ct)).ToHashSet();
        var sessions = await db.Sessions.AsNoTracking()
            .Where(s => s.ProviderId == providerId && s.PatientId == patientId && s.Attendance == "attended"
                && !db.Appointments.Any(a => a.ProviderId == providerId && a.PatientId == patientId && a.SessionId == s.Id))
            .Select(s => new { s.Id, At = s.ActualStart ?? s.CreatedAt })
            .ToListAsync(ct);

        var events = appointments
            .Select(a => new AttendanceEvent(a.Id, PatientSearchQuery.PracticeToday(a.StartsAt), a.StartsAt, KindOf(a.Status, a.CancelledBy, a.SessionId is { } sid && attendedLinked.Contains(sid)), a.CancelledAt))
            .Concat(sessions
                .Where(s => s.At <= now)
                .Select(s => new AttendanceEvent(s.Id, PatientSearchQuery.PracticeToday(s.At), s.At, AttendanceKind.AttendedUnscheduled)))
            .Where(e => (from == null || e.On >= from) && (to == null || e.On <= to))
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .ToList();
        return events;
    }

    // Cancellations by the system count with the provider's.
    private static string KindOf(string status, string? cancelledBy, bool sessionAttended) => status switch
    {
        AppointmentStatus.Attended => AttendanceKind.Attended,
        AppointmentStatus.NoShow => AttendanceKind.NoShow,
        AppointmentStatus.Cancelled => cancelledBy == "patient" ? AttendanceKind.CancelledPatient : AttendanceKind.CancelledProvider,
        // Still scheduled or confirmed, but the session linked to it was recorded as attended.
        _ => sessionAttended ? AttendanceKind.Attended : AttendanceKind.Unrecorded,
    };

    public static async Task<AttendanceSummary?> SummaryAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateOnly? from, DateOnly? to, DateTimeOffset now, CancellationToken ct)
    {
        // Days since the last session look at the whole record up to `to`; counts and intervals are period-bound.
        var all = await EventsAsync(db, providerId, patientId, null, to, now, ct);
        if (all is null)
        {
            return null;
        }
        var inPeriod = AttendanceStats.Compute(all.Where(e => from == null || e.On >= from), now);
        return inPeriod with { DaysSinceLast = AttendanceStats.Compute(all, now).DaysSinceLast };
    }
}
