using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Availability;

// What the calendar draws over a range of local dates (design/specs/2026-11-09-availability-design.md §3): per date, the
// working windows of the version in force on that date (not cut by blocks), the applicable national holiday, and the
// blocks overlapping the range, returned separately so the UI can draw both.
public static class AvailabilityOverlay
{
    public const int MaxRangeDays = 62;

    public static async Task<CalendarOverlayResponse> ForRangeAsync(
        AnamnysDbContext db, Guid providerId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var zone = AvailabilityTime.Provider;
        var rules = await RulesAsync(db, providerId, from, to, ct);
        var settings = await db.ProviderHolidaySettings.AsNoTracking().SingleOrDefaultAsync(s => s.ProviderId == providerId, ct);
        var rangeStart = AvailabilityTime.DayStart(from, zone);
        var rangeEnd = AvailabilityTime.DayStart(to.AddDays(1), zone);
        var blocks = await Blocks.OverlappingAsync(db, providerId, rangeStart, rangeEnd, ct);

        var days = new List<OverlayDay>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var holiday = NationalHolidays.Applicable(date, settings);
            days.Add(new OverlayDay(date, WindowsOn(rules, date), holiday is null ? null : new OverlayHoliday(holiday.Key, holiday.Name)));
        }
        return new CalendarOverlayResponse(days, blocks);
    }

    // The rule rows of every version in force on some date of [from, to].
    internal static Task<List<AvailabilityRule>> RulesAsync(
        AnamnysDbContext db, Guid providerId, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.AvailabilityRules.AsNoTracking()
            .Where(r => r.ProviderId == providerId && r.EffectiveFrom <= to && (r.EffectiveTo == null || r.EffectiveTo >= from))
            .ToListAsync(ct);

    // The working windows of a local date, as instants, each rule converted in its own row's zone.
    internal static List<OverlayWindow> WindowsOn(IEnumerable<AvailabilityRule> rules, DateOnly date) =>
        [.. rules
            .Where(r => r.Weekday == (int)date.DayOfWeek && r.EffectiveFrom <= date && (r.EffectiveTo == null || r.EffectiveTo >= date))
            .Select(r =>
            {
                var zone = AvailabilityTime.Zone(r.Timezone);
                return new OverlayWindow(AvailabilityTime.ToInstant(date, r.StartsAtTime, zone), AvailabilityTime.ToInstant(date, r.EndsAtTime, zone));
            })
            .OrderBy(w => w.StartsAt)];
}
