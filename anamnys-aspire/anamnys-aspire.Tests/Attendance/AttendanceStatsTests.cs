using Anamnys.Server.Attendance;
using FluentAssertions;

namespace Anamnys.Tests.Attendance;

// Pure statistics (design/specs/2026-10-27-attendance-stats-design.md §6).
public class AttendanceStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);

    private static AttendanceEvent Event(string kind, int month, int day, int hour = 13, DateTimeOffset? cancelledAt = null)
    {
        var at = new DateTimeOffset(2026, month, day, hour, 0, 0, TimeSpan.Zero);
        return new AttendanceEvent(Guid.NewGuid(), DateOnly.FromDateTime(at.UtcDateTime), at, kind, cancelledAt);
    }

    [Fact]
    public void Compute_NoEvents_EverythingIsNullOrZero()
    {
        var summary = AttendanceStats.Compute([], Now);

        summary.Counts.Should().Be(new AttendanceCounts(0, 0, 0, 0, 0, 0));
        summary.CancellationLeadHours.Should().BeNull();
        summary.IntervalDays.Should().BeNull();
        summary.DaysSinceLast.Should().BeNull();
    }

    [Fact]
    public void Compute_OddNumberOfIntervals_UsesTheMiddleOne()
    {
        // Attended on 1, 8, 22 Sep and 20 Oct: gaps 7, 14, 28.
        var summary = AttendanceStats.Compute(
            [Event("attended", 9, 1), Event("attended", 9, 8), Event("attended", 9, 22), Event("attended", 10, 20)], Now);

        summary.IntervalDays.Should().Be(new AttendanceIntervals(14, 7, 28, 28));
        summary.DaysSinceLast.Should().Be(6);
    }

    [Fact]
    public void Compute_EvenNumberOfIntervals_AveragesTheMiddleTwo()
    {
        // Gaps 7 and 14.
        var summary = AttendanceStats.Compute([Event("attended", 9, 1), Event("attended", 9, 8), Event("attended", 9, 22)], Now);

        summary.IntervalDays.Should().Be(new AttendanceIntervals(10.5, 7, 14, 14));
    }

    [Fact]
    public void Compute_OneAttended_HasNoIntervalsButDaysSinceLast()
    {
        var summary = AttendanceStats.Compute([Event("attended", 10, 19)], Now);

        summary.IntervalDays.Should().BeNull();
        summary.DaysSinceLast.Should().Be(7);
    }

    [Fact]
    public void Compute_UnscheduledSessions_CountAsAttendedForIntervals()
    {
        var summary = AttendanceStats.Compute([Event("attended", 10, 1), Event("attended_unscheduled", 10, 11)], Now);

        summary.Counts.Attended.Should().Be(1);
        summary.Counts.AttendedUnscheduled.Should().Be(1);
        summary.IntervalDays.Should().Be(new AttendanceIntervals(10, 10, 10, 10));
    }

    [Fact]
    public void Compute_UnrecordedAndNoShow_AreNeitherAttendedNorIntervals()
    {
        var summary = AttendanceStats.Compute([Event("attended", 10, 1), Event("unrecorded", 10, 8), Event("no_show", 10, 15)], Now);

        summary.Counts.Should().Be(new AttendanceCounts(1, 0, 1, 0, 0, 1));
        summary.IntervalDays.Should().BeNull();
        summary.DaysSinceLast.Should().Be(25);
    }

    [Fact]
    public void Compute_TwoAttendedOnTheSameDay_GiveAZeroDayInterval()
    {
        var summary = AttendanceStats.Compute([Event("attended", 10, 5, 10), Event("attended_unscheduled", 10, 5, 14)], Now);

        summary.IntervalDays.Should().Be(new AttendanceIntervals(0, 0, 0, 0));
    }

    [Fact]
    public void Compute_FutureEvents_AreExcluded()
    {
        var summary = AttendanceStats.Compute([Event("attended", 10, 1), Event("unrecorded", 10, 27), Event("no_show", 11, 3)], Now);

        summary.Counts.Should().Be(new AttendanceCounts(1, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void Compute_CancellationLead_IsMedianMinMaxAndClampedAtZero()
    {
        // Leads: 24 h, 2 h, cancelled after the start (clamped to 0) and 48 h; a cancellation without a time is skipped.
        var summary = AttendanceStats.Compute(
            [
                Event("cancelled_provider", 10, 5, 13, new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero)),
                Event("cancelled_patient", 10, 6, 13, new DateTimeOffset(2026, 10, 6, 11, 0, 0, TimeSpan.Zero)),
                Event("cancelled_patient", 10, 7, 13, new DateTimeOffset(2026, 10, 7, 14, 0, 0, TimeSpan.Zero)),
                Event("cancelled_provider", 10, 8, 13, new DateTimeOffset(2026, 10, 6, 13, 0, 0, TimeSpan.Zero)),
                Event("cancelled_provider", 10, 9),
            ], Now);

        summary.Counts.CancelledByProvider.Should().Be(3);
        summary.Counts.CancelledByPatient.Should().Be(2);
        summary.CancellationLeadHours.Should().Be(new CancellationLead(13, 0, 48, 4));
    }
}
