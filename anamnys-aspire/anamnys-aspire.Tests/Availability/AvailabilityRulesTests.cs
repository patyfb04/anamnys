using Anamnys.Server.Availability;
using FluentAssertions;
using static Anamnys.Tests.Availability.AvailabilityScene;

namespace Anamnys.Tests.Availability;

// design/specs/2026-11-09-availability-design.md §3, §6: versioning of the weekly hours and the holiday preferences, on
// a scratch database.
[Collection(SharedAppHostCollection.Name)]
public class AvailabilityRulesTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Save_FromD_ClosesTheOpenVersionAtDMinus1_AndLeavesThePastUntouched()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await s.AddRuleAsync(1, "07:00", "11:00", new DateOnly(2026, 1, 5), new DateOnly(2026, 6, 30), Ct); // an old, closed version
        await s.AddRuleAsync(1, "09:00", "17:00", new DateOnly(2026, 7, 1), null, Ct); // the open one
        var d = Today.AddDays(7);

        // Act
        await s.SaveWeekdaysAsync(d, Ct);

        // Assert
        var rules = await s.RulesAsync(Ct);
        rules.Where(r => r.From == new DateOnly(2026, 1, 5)).Should().ContainSingle().Which.To.Should().Be(new DateOnly(2026, 6, 30));
        rules.Where(r => r.From == new DateOnly(2026, 7, 1)).Should().ContainSingle().Which.To.Should().Be(d.AddDays(-1));
        rules.Where(r => r.From == d).Should().HaveCount(10).And.OnlyContain(r => r.To == null);
        (await s.AuditAsync(Ct)).Should().ContainSingle().Which.Should().Be($"availability_changed availability from {d:yyyy-MM-dd}");
    }

    [Fact]
    public async Task Save_FromD_ReplacesVersionsStartingAfterD_AndCancelReopensThePreviousOne()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(Today, Ct);
        await s.SaveWeekdaysAsync(Today.AddDays(30), Ct, start: "09:00");
        var d = Today.AddDays(10);

        // Act
        await s.SaveWeekdaysAsync(d, Ct, start: "10:00");
        var afterReplace = await s.RulesAsync(Ct);
        AvailabilityOutcome cancelled, again, past;
        await using (var db = s.CreateDbContext())
        {
            cancelled = await AvailabilityRules.CancelUpcomingAsync(db, s.Provider, d, Now, Ct);
            again = await AvailabilityRules.CancelUpcomingAsync(db, s.Provider, d, Now, Ct);
            past = await AvailabilityRules.CancelUpcomingAsync(db, s.Provider, Today, Now, Ct);
        }

        // Assert
        afterReplace.Select(r => r.From).Distinct().Should().Equal(Today, d);
        afterReplace.Where(r => r.From == Today).Should().OnlyContain(r => r.To == d.AddDays(-1));
        afterReplace.Where(r => r.From == d).Should().Contain(r => r.Start == new TimeOnly(10, 0));
        cancelled.Should().Be(AvailabilityOutcome.Ok);
        again.Should().Be(AvailabilityOutcome.NotFound);
        past.Should().Be(AvailabilityOutcome.NotFound);
        var final = await s.RulesAsync(Ct);
        final.Select(r => r.From).Distinct().Should().Equal(Today);
        final.Should().OnlyContain(r => r.To == null);
    }

    [Fact]
    public async Task CancelUpcoming_ABetweenVersion_GivesThePreviousOneItsEndBack()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await s.AddRuleAsync(1, "08:00", "12:00", Today, Today.AddDays(9), Ct);
        await s.AddRuleAsync(1, "09:00", "12:00", Today.AddDays(10), Today.AddDays(19), Ct);
        await s.AddRuleAsync(1, "10:00", "12:00", Today.AddDays(20), null, Ct);

        // Act
        await using (var db = s.CreateDbContext())
        {
            (await AvailabilityRules.CancelUpcomingAsync(db, s.Provider, Today.AddDays(10), Now, Ct)).Should().Be(AvailabilityOutcome.Ok);
        }

        // Assert
        (await s.RulesAsync(Ct)).Select(r => (r.From, r.To)).Should().Equal((Today, Today.AddDays(19)), (Today.AddDays(20), (DateOnly?)null));
    }

    [Fact]
    public async Task Save_InThePastOrInvalid_IsRefused_AndChangesNothing()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(Today, Ct);
        await using var db = s.CreateDbContext();
        SaveAvailabilityDay?[] overlap = [new SaveAvailabilityDay(1, [new("08:00", "12:00"), new("11:00", "13:00")])];

        // Act
        var past = await AvailabilityRules.SaveAsync(db, s.Provider, new SaveAvailabilityRequest(Today.AddDays(-1), []), Now, Ct);
        var invalid = await AvailabilityRules.SaveAsync(db, s.Provider, new SaveAvailabilityRequest(Today.AddDays(1), overlap), Now, Ct);
        var missing = await AvailabilityRules.SaveAsync(db, s.Provider, new SaveAvailabilityRequest(null, []), Now, Ct);

        // Assert
        past.Should().Be(AvailabilityOutcome.EffectiveInPast);
        invalid.Should().Be(AvailabilityOutcome.InvalidIntervals);
        missing.Should().Be(AvailabilityOutcome.InvalidEffectiveFrom);
        (await s.RulesAsync(Ct)).Should().HaveCount(10).And.OnlyContain(r => r.From == Today && r.To == null);
    }

    [Fact]
    public async Task Get_ReturnsTheCurrentVersionWithSevenDaysAndTheUpcomingOnes()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await s.AddRuleAsync(1, "07:00", "11:00", new DateOnly(2026, 1, 5), Today.AddDays(-1), Ct);
        await s.SaveWeekdaysAsync(Today, Ct);
        await s.SaveWeekdaysAsync(Today.AddDays(14), Ct, start: "09:00");
        await using var db = s.CreateDbContext();

        // Act
        var answer = await AvailabilityRules.GetAsync(db, s.Provider, Now, Ct);
        var none = await AvailabilityRules.GetAsync(db, s.OtherProvider, Now, Ct);

        // Assert
        answer.Current!.EffectiveFrom.Should().Be(Today);
        answer.Current.Days.Select(d => d.Weekday).Should().Equal(0, 1, 2, 3, 4, 5, 6);
        answer.Current.Days[0].Intervals.Should().BeEmpty();
        answer.Current.Days[1].Intervals.Should().Equal(new AvailabilityInterval("08:00", "12:00"), new AvailabilityInterval("14:00", "18:00"));
        answer.Upcoming.Should().ContainSingle().Which.EffectiveFrom.Should().Be(Today.AddDays(14));
        answer.Upcoming[0].Days[1].Intervals[0].Start.Should().Be("09:00");
        none.Current.Should().BeNull();
        none.Upcoming.Should().BeEmpty();
    }

    [Fact]
    public async Task AllClosedVersion_IsShownWarnsAfterDAndCanBeCancelled()
    {
        // Arrange: hours from today, then "Não atendo" every day from next Monday (23/11).
        await using var s = await CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(Today, Ct);
        var d = new DateOnly(2026, 11, 23);
        await using var db = s.CreateDbContext();
        var saved = await AvailabilityRules.SaveAsync(db, s.Provider, new SaveAvailabilityRequest(d, []), Now, Ct);
        var missingDays = await AvailabilityRules.SaveAsync(db, s.Provider, new SaveAvailabilityRequest(d, null), Now, Ct);

        // Act
        var get = await AvailabilityRules.GetAsync(db, s.Provider, Now, Ct);
        var before = await AvailabilityCheck.ReasonsAsync(db, s.Provider, new DateTimeOffset(2026, 11, 16, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 16, 13, 50, 0, TimeSpan.Zero), Ct);
        var after = await AvailabilityCheck.ReasonsAsync(db, s.Provider, new DateTimeOffset(2026, 11, 23, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 23, 13, 50, 0, TimeSpan.Zero), Ct);
        var cancelled = await AvailabilityRules.CancelUpcomingAsync(db, s.Provider, d, Now, Ct);
        var afterCancel = await AvailabilityRules.GetAsync(db, s.Provider, Now, Ct);

        // Assert
        saved.Should().Be(AvailabilityOutcome.Ok);
        missingDays.Should().Be(AvailabilityOutcome.InvalidIntervals);
        get.Current!.EffectiveFrom.Should().Be(Today);
        get.Upcoming.Should().ContainSingle().Which.EffectiveFrom.Should().Be(d);
        get.Upcoming[0].Days.Should().HaveCount(7).And.OnlyContain(day => day.Intervals.Count == 0);
        before.Should().BeEmpty();
        after.Should().Equal(new OutsideAvailabilityReason("outside_hours"));
        cancelled.Should().Be(AvailabilityOutcome.Ok);
        afterCancel.Upcoming.Should().BeEmpty();
        (await s.VersionsAsync(Ct)).Should().Equal((Today, (DateOnly?)null));
        (await s.RulesAsync(Ct)).Should().HaveCount(10).And.OnlyContain(r => r.To == null);
    }

    [Fact]
    public async Task Check_BeforeTheFirstVersionStarts_HasNoOutsideHours()
    {
        // Arrange: the only version starts on 23/11.
        await using var s = await CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(new DateOnly(2026, 11, 23), Ct);
        await using var db = s.CreateDbContext();

        // Act: Saturday 14/11 and Saturday 28/11, 10:00.
        var early = await AvailabilityCheck.ReasonsAsync(db, s.Provider, new DateTimeOffset(2026, 11, 14, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 14, 13, 50, 0, TimeSpan.Zero), Ct);
        var later = await AvailabilityCheck.ReasonsAsync(db, s.Provider, new DateTimeOffset(2026, 11, 28, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 28, 13, 50, 0, TimeSpan.Zero), Ct);

        // Assert
        early.Should().BeEmpty();
        later.Should().Equal(new OutsideAvailabilityReason("outside_hours"));
    }

    [Fact]
    public async Task HolidaySettings_DefaultSaveAndRefuseUnknownKeys()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var before = await AvailabilityRules.GetHolidaySettingsAsync(db, s.Provider, Now, Ct);
        var saved = await AvailabilityRules.SaveHolidaySettingsAsync(db, s.Provider, new SaveHolidaySettingsRequest(false, ["tiradentes", "carnaval_segunda", "tiradentes"]), Now, Ct);
        var unknown = await AvailabilityRules.SaveHolidaySettingsAsync(db, s.Provider, new SaveHolidaySettingsRequest(true, ["aniversario"]), Now, Ct);
        var missing = await AvailabilityRules.SaveHolidaySettingsAsync(db, s.Provider, new SaveHolidaySettingsRequest(null, []), Now, Ct);
        var after = await AvailabilityRules.GetHolidaySettingsAsync(db, s.Provider, Now, Ct);

        // Assert
        before.WorksOnHolidays.Should().BeFalse();
        before.DisabledHolidays.Should().BeEmpty();
        before.Holidays.Should().HaveCount(26);
        before.Holidays.Select(h => h.Date.Year).Distinct().Should().Equal(2026, 2027);
        saved.Should().Be(AvailabilityOutcome.Ok);
        unknown.Should().Be(AvailabilityOutcome.InvalidHolidays);
        missing.Should().Be(AvailabilityOutcome.InvalidHolidays);
        after.WorksOnHolidays.Should().BeFalse();
        after.DisabledHolidays.Should().Equal("carnaval_segunda", "tiradentes");
        (await s.AuditAsync(Ct)).Should().ContainSingle().Which.Should().StartWith("availability_changed holidays");
    }
}
