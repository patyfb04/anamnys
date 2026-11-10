using Anamnys.Server.Availability;
using FluentAssertions;

namespace Anamnys.Tests.Availability;

// design/specs/2026-11-09-availability-design.md §3, §6: interval validation, block normalisation and DST-safe local
// time conversion, without a database.
public class AvailabilityValidationTests
{
    private static SaveAvailabilityDay Day(int weekday, params (string Start, string End)[] intervals) =>
        new(weekday, [.. intervals.Select(i => new SaveAvailabilityInterval(i.Start, i.End))]);

    [Fact]
    public void ParseDays_ValidDays_SortsIntervalsAndKeepsEmptyDays()
    {
        // Act
        var days = AvailabilityRules.ParseDays([Day(1, ("14:00", "18:00"), ("08:00", "12:00")), Day(2), Day(0, ("09:05", "09:55"))]);

        // Assert
        days.Should().NotBeNull();
        days![1].Select(i => i.Start).Should().Equal(new TimeOnly(8, 0), new TimeOnly(14, 0));
        days[2].Should().BeEmpty();
        days[0].Single().End.Should().Be(new TimeOnly(9, 55));
    }

    [Fact]
    public void ParseDays_BackToBackIntervals_AreAllowed()
    {
        AvailabilityRules.ParseDays([Day(3, ("08:00", "12:00"), ("12:00", "13:00"))]).Should().NotBeNull();
    }

    [Theory]
    [InlineData("08:00", "12:00", "11:00", "13:00")] // overlap
    [InlineData("12:00", "12:00", null, null)] // start = end
    [InlineData("13:00", "12:00", null, null)] // start > end
    [InlineData("08:03", "12:00", null, null)] // not on a 5-minute step
    [InlineData("08:00", "12:07", null, null)]
    [InlineData("8h", "12:00", null, null)] // not HH:mm
    [InlineData(null, "12:00", null, null)]
    public void ParseDays_InvalidIntervals_AreRefused(string? start1, string? end1, string? start2, string? end2)
    {
        // Arrange
        var intervals = new List<SaveAvailabilityInterval?> { new(start1, end1) };
        if (start2 is not null)
        {
            intervals.Add(new SaveAvailabilityInterval(start2, end2));
        }

        // Act
        var days = AvailabilityRules.ParseDays([new SaveAvailabilityDay(1, intervals)]);

        // Assert
        days.Should().BeNull();
    }

    [Fact]
    public void ParseDays_MoreThanSixIntervals_OrABadOrRepeatedWeekday_AreRefused()
    {
        // Arrange
        var seven = Enumerable.Range(0, 7).Select(i => ($"{8 + i:00}:00", $"{8 + i:00}:30")).ToArray();
        var six = seven.Take(6).ToArray();

        // Act + Assert
        AvailabilityRules.ParseDays([Day(1, seven)]).Should().BeNull();
        AvailabilityRules.ParseDays([Day(1, six)]).Should().NotBeNull();
        AvailabilityRules.ParseDays([Day(7, ("08:00", "09:00"))]).Should().BeNull();
        AvailabilityRules.ParseDays([new SaveAvailabilityDay(null, [])]).Should().BeNull();
        AvailabilityRules.ParseDays([Day(1, ("08:00", "09:00")), Day(1, ("10:00", "11:00"))]).Should().BeNull();
    }

    [Fact]
    public void ToInstant_IsDstSafe_ForAGapAndARepeatedHour()
    {
        // Arrange
        var newYork = AvailabilityTime.Zone("America/New_York");

        // Act
        var beforeChange = AvailabilityTime.ToInstant(new DateOnly(2027, 3, 13), new TimeOnly(9, 0), newYork);
        var afterChange = AvailabilityTime.ToInstant(new DateOnly(2027, 3, 14), new TimeOnly(9, 0), newYork);
        var inGap = AvailabilityTime.ToInstant(new DateOnly(2027, 3, 14), new TimeOnly(2, 30), newYork);
        var repeated = AvailabilityTime.ToInstant(new DateOnly(2027, 11, 7), new TimeOnly(1, 30), newYork);
        var saoPaulo = AvailabilityTime.DayStart(new DateOnly(2026, 11, 9), AvailabilityTime.Provider);

        // Assert
        beforeChange.Should().Be(new DateTimeOffset(2027, 3, 13, 14, 0, 0, TimeSpan.Zero));
        afterChange.Should().Be(new DateTimeOffset(2027, 3, 14, 13, 0, 0, TimeSpan.Zero));
        inGap.Should().Be(new DateTimeOffset(2027, 3, 14, 7, 30, 0, TimeSpan.Zero)); // 03:30 EDT, just after the gap
        repeated.Should().Be(new DateTimeOffset(2027, 11, 7, 5, 30, 0, TimeSpan.Zero)); // the first 01:30 (EDT)
        saoPaulo.Should().Be(new DateTimeOffset(2026, 11, 9, 3, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Normalize_AllDayBlock_RunsFromLocalMidnightToTheMidnightAfterTheLastDate()
    {
        // Act
        var (outcome, block) = Blocks.Normalize(new BlockRequest(
            "vacation", new DateTimeOffset(2027, 1, 10, 15, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 20, 15, 0, 0, TimeSpan.Zero),
            AllDay: true, Reason: "  Férias de janeiro  "));

        // Assert
        outcome.Should().Be(BlockOutcome.Ok);
        block!.StartsAt.Should().Be(new DateTimeOffset(2027, 1, 10, 3, 0, 0, TimeSpan.Zero));
        block.EndsAt.Should().Be(new DateTimeOffset(2027, 1, 21, 3, 0, 0, TimeSpan.Zero));
        block.Reason.Should().Be("Férias de janeiro");
        block.AllDay.Should().BeTrue();
    }

    [Fact]
    public void Normalize_BadBlocks_AnswerTheirOutcome()
    {
        // Arrange
        var at = new DateTimeOffset(2027, 1, 10, 15, 0, 0, TimeSpan.Zero);

        // Act + Assert
        Blocks.Normalize(new BlockRequest("ferias", at, at.AddHours(1))).Outcome.Should().Be(BlockOutcome.InvalidKind);
        Blocks.Normalize(new BlockRequest(null, at, at.AddHours(1))).Outcome.Should().Be(BlockOutcome.InvalidKind);
        Blocks.Normalize(null).Outcome.Should().Be(BlockOutcome.InvalidKind);
        Blocks.Normalize(new BlockRequest("block", at, at)).Outcome.Should().Be(BlockOutcome.InvalidRange);
        Blocks.Normalize(new BlockRequest("block", at, at.AddHours(-1))).Outcome.Should().Be(BlockOutcome.InvalidRange);
        Blocks.Normalize(new BlockRequest("block", at, null)).Outcome.Should().Be(BlockOutcome.InvalidRange);
        Blocks.Normalize(new BlockRequest("block", at, at.AddDays(-1), AllDay: true)).Outcome.Should().Be(BlockOutcome.InvalidRange);
        Blocks.Normalize(new BlockRequest("block", at, at.AddDays(400))).Outcome.Should().Be(BlockOutcome.InvalidRange);
        Blocks.Normalize(new BlockRequest("block", at, at.AddHours(1), Reason: new string('x', 201))).Outcome.Should().Be(BlockOutcome.InvalidReason);
        Blocks.Normalize(new BlockRequest("holiday", at, at, AllDay: true)).Outcome.Should().Be(BlockOutcome.Ok);
    }

    [Fact]
    public void Covers_JoinsTouchingWindowsAndFindsGaps()
    {
        // Arrange
        var t = new DateTimeOffset(2027, 1, 11, 11, 0, 0, TimeSpan.Zero);
        OverlayWindow[] windows = [new(t, t.AddHours(2)), new(t.AddHours(2), t.AddHours(3)), new(t.AddHours(4), t.AddHours(5))];

        // Act + Assert
        AvailabilityCheck.Covers(windows, t.AddMinutes(90), t.AddMinutes(150)).Should().BeTrue();
        AvailabilityCheck.Covers(windows, t, t.AddHours(3)).Should().BeTrue();
        AvailabilityCheck.Covers(windows, t.AddMinutes(150), t.AddMinutes(210)).Should().BeFalse();
        AvailabilityCheck.Covers(windows, t.AddMinutes(-10), t.AddMinutes(30)).Should().BeFalse();
        AvailabilityCheck.Covers([], t, t.AddHours(1)).Should().BeFalse();
    }
}
