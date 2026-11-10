using Anamnys.Server.Availability;
using FluentAssertions;
using static Anamnys.Tests.Availability.AvailabilityScene;

namespace Anamnys.Tests.Availability;

// design/specs/2026-11-09-availability-design.md §3, §6: the calendar overlay and the outside-availability check, on a
// scratch database.
[Collection(SharedAppHostCollection.Name)]
public class AvailabilityOverlayTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // 2026-11-16 is a Monday; 10:00 in São Paulo is 13:00Z.
    private static readonly DateTimeOffset Monday10 = new(2026, 11, 16, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Overlay_WindowsFollowTheVersionInForceOnEachDate()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(Today, Ct);
        await s.SaveWeekdaysAsync(new DateOnly(2026, 11, 18), Ct, start: "10:00"); // from Wednesday
        await using var db = s.CreateDbContext();

        // Act
        var overlay = await AvailabilityOverlay.ForRangeAsync(db, s.Provider, new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 22), Ct);

        // Assert
        overlay.Days.Select(d => d.Date).Should().Equal(Enumerable.Range(0, 7).Select(i => new DateOnly(2026, 11, 16).AddDays(i)));
        var monday = overlay.Days[0];
        monday.Windows.Should().Equal(
            new OverlayWindow(new DateTimeOffset(2026, 11, 16, 11, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero)),
            new OverlayWindow(new DateTimeOffset(2026, 11, 16, 17, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 16, 21, 0, 0, TimeSpan.Zero)));
        overlay.Days[2].Windows[0].StartsAt.Should().Be(new DateTimeOffset(2026, 11, 18, 13, 0, 0, TimeSpan.Zero));
        overlay.Days[5].Windows.Should().BeEmpty(); // Saturday
        overlay.Days[4].Holiday.Should().Be(new OverlayHoliday("consciencia_negra", "Dia Nacional de Zumbi e da Consciência Negra"));
        overlay.Days.Where(d => d.Date != new DateOnly(2026, 11, 20)).Should().OnlyContain(d => d.Holiday == null);
    }

    [Fact]
    public async Task Overlay_HolidayPreferences_DisableOneOrAll()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        var from = new DateOnly(2026, 11, 14);
        var to = new DateOnly(2026, 11, 21);

        // Act
        var byDefault = await AvailabilityOverlay.ForRangeAsync(db, s.Provider, from, to, Ct);
        await AvailabilityRules.SaveHolidaySettingsAsync(db, s.Provider, new SaveHolidaySettingsRequest(false, ["proclamacao_da_republica"]), Now, Ct);
        var oneOff = await AvailabilityOverlay.ForRangeAsync(db, s.Provider, from, to, Ct);
        await AvailabilityRules.SaveHolidaySettingsAsync(db, s.Provider, new SaveHolidaySettingsRequest(true, []), Now, Ct);
        var works = await AvailabilityOverlay.ForRangeAsync(db, s.Provider, from, to, Ct);

        // Assert
        byDefault.Days.Where(d => d.Holiday != null).Select(d => d.Holiday!.Key).Should().Equal("proclamacao_da_republica", "consciencia_negra");
        oneOff.Days.Where(d => d.Holiday != null).Select(d => d.Holiday!.Key).Should().Equal("consciencia_negra");
        works.Days.Should().OnlyContain(d => d.Holiday == null);
    }

    [Fact]
    public async Task Overlay_ReturnsBlocksOverlappingTheRangeEdges_AndNotOthersOrForeignOnes()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        var rangeStart = new DateTimeOffset(2026, 11, 16, 3, 0, 0, TimeSpan.Zero); // 16/11 00:00 local
        var rangeEnd = new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero); // 23/11 00:00 local
        var acrossStart = await s.AddBlockAsync(s.Provider, rangeStart.AddDays(-2), rangeStart.AddHours(1), Ct, "vacation");
        var acrossEnd = await s.AddBlockAsync(s.Provider, rangeEnd.AddHours(-1), rangeEnd.AddDays(2), Ct);
        await s.AddBlockAsync(s.Provider, rangeStart.AddDays(-3), rangeStart, Ct); // ends exactly at the start
        await s.AddBlockAsync(s.Provider, rangeEnd, rangeEnd.AddDays(1), Ct); // starts exactly at the end
        await s.AddBlockAsync(s.OtherProvider, rangeStart.AddDays(1), rangeStart.AddDays(2), Ct);
        await using var db = s.CreateDbContext();

        // Act
        var overlay = await AvailabilityOverlay.ForRangeAsync(db, s.Provider, new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 22), Ct);

        // Assert
        overlay.Blocks.Select(b => b.Id).Should().Equal(acrossEnd, acrossStart); // newest first
        overlay.Blocks[1].Kind.Should().Be("vacation");
    }

    [Fact]
    public async Task Overlay_ConvertsEachRuleInItsZone_AcrossADstChange()
    {
        // Arrange: a version in New York's zone across the 2027-03-14 change (EST -5 to EDT -4).
        await using var s = await CreateAsync(fixture, Ct);
        foreach (var weekday in new[] { 6, 0, 1 })
        {
            await s.AddRuleAsync(weekday, "09:00", "12:00", new DateOnly(2027, 3, 1), null, Ct, zone: "America/New_York");
        }
        await using var db = s.CreateDbContext();

        // Act
        var overlay = await AvailabilityOverlay.ForRangeAsync(db, s.Provider, new DateOnly(2027, 3, 13), new DateOnly(2027, 3, 15), Ct);

        // Assert
        overlay.Days.Select(d => d.Windows.Single().StartsAt.UtcDateTime.Hour).Should().Equal(14, 13, 13);
        overlay.Days.Select(d => d.Windows.Single().EndsAt.UtcDateTime.Hour).Should().Equal(17, 16, 16);
    }

    [Fact]
    public async Task Check_FindsOutsideHoursBlocksAndHolidays_AndNothingInsideTheWindows()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(Today, Ct);
        var block = await s.AddBlockAsync(s.Provider, Monday10.AddDays(1), Monday10.AddDays(1).AddHours(2), Ct, "vacation");
        await using var db = s.CreateDbContext();

        // Act
        var inside = await AvailabilityCheck.ReasonsAsync(db, s.Provider, Monday10, Monday10.AddMinutes(50), Ct);
        var lunch = await AvailabilityCheck.ReasonsAsync(db, s.Provider, Monday10.AddMinutes(150), Monday10.AddMinutes(200), Ct); // 12:30
        var straddling = await AvailabilityCheck.ReasonsAsync(db, s.Provider, Monday10.AddMinutes(100), Monday10.AddMinutes(150), Ct); // 11:40-12:30
        var saturday = await AvailabilityCheck.ReasonsAsync(db, s.Provider, Monday10.AddDays(-2), Monday10.AddDays(-2).AddMinutes(50), Ct);
        var inBlock = await AvailabilityCheck.ReasonsAsync(db, s.Provider, Monday10.AddDays(1).AddMinutes(30), Monday10.AddDays(1).AddMinutes(80), Ct);
        var onHoliday = await AvailabilityCheck.ReasonsAsync(db, s.Provider, Monday10.AddDays(4), Monday10.AddDays(4).AddMinutes(50), Ct); // 20/11
        var foreignProvider = await AvailabilityCheck.ReasonsAsync(db, s.OtherProvider, Monday10.AddDays(1).AddMinutes(30), Monday10.AddDays(1).AddMinutes(80), Ct);

        // Assert
        inside.Should().BeEmpty();
        lunch.Should().Equal(new OutsideAvailabilityReason("outside_hours"));
        straddling.Should().Equal(new OutsideAvailabilityReason("outside_hours"));
        saturday.Should().Equal(new OutsideAvailabilityReason("outside_hours"));
        inBlock.Should().Equal(new OutsideAvailabilityReason("block", Monday10.AddDays(1), Monday10.AddDays(1).AddHours(2), BlockKind: "vacation"));
        onHoliday.Should().Equal(new OutsideAvailabilityReason(
            "holiday", new DateTimeOffset(2026, 11, 20, 3, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero),
            HolidayName: "Dia Nacional de Zumbi e da Consciência Negra"));
        foreignProvider.Should().BeEmpty(); // no hours saved, no blocks of their own
        block.Should().NotBeEmpty();
    }
}
