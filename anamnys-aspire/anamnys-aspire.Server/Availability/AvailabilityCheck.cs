using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Availability;

// The outside-availability check of create and reschedule (design/specs/2026-11-09-availability-design.md §3). An
// appointment is outside when any part of it falls outside the windows of its days, when it overlaps a block, or when
// a day it touches is an applicable national holiday. A provider with no version of weekly hours in force by then has no windows to fall
// outside of, so only blocks and holidays apply to them. Status changes never run this check.
public static class AvailabilityCheck
{
    public const string OutsideAction = "appointment_outside_availability";

    public static async Task<IReadOnlyList<OutsideAvailabilityReason>> ReasonsAsync(
        AnamnysDbContext db, Guid providerId, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct)
    {
        var zone = AvailabilityTime.Provider;
        startsAt = startsAt.ToUniversalTime();
        endsAt = endsAt.ToUniversalTime();
        var firstDay = AvailabilityTime.LocalDate(startsAt, zone);
        var lastDay = AvailabilityTime.LocalDate(endsAt.AddTicks(-1), zone);
        var reasons = new List<OutsideAvailabilityReason>();

        var configured = await AvailabilityRules.HasHoursByAsync(db, providerId, lastDay, ct);
        if (configured)
        {
            var rules = await AvailabilityOverlay.RulesAsync(db, providerId, firstDay, lastDay, ct);
            var windows = new List<OverlayWindow>();
            for (var date = firstDay; date <= lastDay; date = date.AddDays(1))
            {
                windows.AddRange(AvailabilityOverlay.WindowsOn(rules, date));
            }
            if (!Covers(windows, startsAt, endsAt))
            {
                reasons.Add(new OutsideAvailabilityReason(OutsideAvailabilityReason.OutsideHours));
            }
        }

        foreach (var block in await Blocks.OverlappingAsync(db, providerId, startsAt, endsAt, ct))
        {
            reasons.Add(new OutsideAvailabilityReason(OutsideAvailabilityReason.Block, block.StartsAt, block.EndsAt, BlockKind: block.Kind));
        }

        var settings = await db.ProviderHolidaySettings.AsNoTracking().SingleOrDefaultAsync(s => s.ProviderId == providerId, ct);
        for (var date = firstDay; date <= lastDay; date = date.AddDays(1))
        {
            if (NationalHolidays.Applicable(date, settings) is { } holiday)
            {
                reasons.Add(new OutsideAvailabilityReason(
                    OutsideAvailabilityReason.Holiday, AvailabilityTime.DayStart(date, zone), AvailabilityTime.DayStart(date.AddDays(1), zone),
                    HolidayName: holiday.Name));
            }
        }
        return reasons;
    }

    // Whether the windows, joined where they touch, cover [startsAt, endsAt) without a gap.
    internal static bool Covers(IEnumerable<OverlayWindow> windows, DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        var cursor = startsAt;
        foreach (var window in windows.OrderBy(w => w.StartsAt))
        {
            if (window.StartsAt > cursor)
            {
                break;
            }
            if (window.EndsAt > cursor)
            {
                cursor = window.EndsAt;
            }
            if (cursor >= endsAt)
            {
                return true;
            }
        }
        return cursor >= endsAt;
    }

    // The audit entry of an appointment saved despite the warning: its id and the reason kinds only.
    internal static void AddAudit(
        AnamnysDbContext db, Guid providerId, Guid appointmentId, IEnumerable<OutsideAvailabilityReason> reasons, DateTimeOffset at) =>
        AvailabilityRules.AddAudit(db, providerId, OutsideAction, "appointment",
            $"{appointmentId} {string.Join(",", reasons.Select(r => r.Kind).Distinct())}", at);
}
