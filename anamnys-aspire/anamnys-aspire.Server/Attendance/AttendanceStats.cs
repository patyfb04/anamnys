namespace Anamnys.Server.Attendance;

public static class AttendanceKind
{
    public const string Attended = "attended";
    public const string AttendedUnscheduled = "attended_unscheduled";
    public const string NoShow = "no_show";
    public const string CancelledProvider = "cancelled_provider";
    public const string CancelledPatient = "cancelled_patient";
    public const string Unrecorded = "unrecorded";
}

// One appointment or one session with no appointment. On is the practice-local day, At the instant it started.
public sealed record AttendanceEvent(Guid Id, DateOnly On, DateTimeOffset At, string Kind, DateTimeOffset? CancelledAt = null);

public sealed record AttendanceCounts(
    int Attended, int AttendedUnscheduled, int NoShow, int CancelledByProvider, int CancelledByPatient, int Unrecorded);

public sealed record CancellationLead(double Median, double Min, double Max, int Count);

public sealed record AttendanceIntervals(double Median, int Min, int Max, int Last);

public sealed record AttendanceSummary(
    AttendanceCounts Counts, CancellationLead? CancellationLeadHours, AttendanceIntervals? IntervalDays, int? DaysSinceLast);

// design/specs/2026-10-27-attendance-stats-design.md §3: pure statistics over attendance events. Observation only.
public static class AttendanceStats
{
    public static AttendanceSummary Compute(IEnumerable<AttendanceEvent> events, DateTimeOffset now)
    {
        var today = Patients.PatientSearchQuery.PracticeToday(now);
        var past = events.Where(e => e.At <= now).ToList();
        int Count(string kind) => past.Count(e => e.Kind == kind);

        var counts = new AttendanceCounts(
            Count(AttendanceKind.Attended), Count(AttendanceKind.AttendedUnscheduled), Count(AttendanceKind.NoShow),
            Count(AttendanceKind.CancelledProvider), Count(AttendanceKind.CancelledPatient), Count(AttendanceKind.Unrecorded));

        var leads = past
            .Where(e => e.Kind is AttendanceKind.CancelledProvider or AttendanceKind.CancelledPatient && e.CancelledAt != null)
            .Select(e => Math.Max(0, (e.At - e.CancelledAt!.Value).TotalHours))
            .OrderBy(h => h)
            .ToList();
        var lead = leads.Count == 0 ? null : new CancellationLead(Median(leads), leads[0], leads[^1], leads.Count);

        var days = past
            .Where(e => e.Kind is AttendanceKind.Attended or AttendanceKind.AttendedUnscheduled)
            .Select(e => e.On)
            .OrderBy(d => d)
            .ToList();
        var gaps = days.Zip(days.Skip(1), (a, b) => b.DayNumber - a.DayNumber).ToList();
        var intervals = gaps.Count == 0
            ? null
            : new AttendanceIntervals(Median(gaps.Order().Select(g => (double)g).ToList()), gaps.Min(), gaps.Max(), gaps[^1]);

        int? since = days.Count == 0 ? null : Math.Max(0, today.DayNumber - days[^1].DayNumber);
        return new AttendanceSummary(counts, lead, intervals, since);
    }

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}
