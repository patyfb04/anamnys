using System.Globalization;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Availability;

public enum AvailabilityOutcome { Ok, NotFound, InvalidIntervals, EffectiveInPast, InvalidEffectiveFrom, InvalidHolidays }

// The provider's weekly hours, versioned by "a partir de" date (design/specs/2026-11-09-availability-design.md §3).
// A version is the set of rows sharing EffectiveFrom. Saving from D closes every version open on D at D - 1, deletes the
// versions starting on or after D (replaced by the newer decision) and writes the new rows from D, open-ended. Nothing
// before D changes; D is never before the provider's local today.
public static class AvailabilityRules
{
    public const string ChangedAction = "availability_changed";
    public const string AuditField = "availability";
    public const int MaxIntervalsPerDay = 6;
    public const int StepMinutes = 5;

    public sealed record Interval(TimeOnly Start, TimeOnly End);

    // Parses and checks the days of a request: weekdays 0-6 at most once, "HH:mm" times on 5-minute steps, start before
    // end, no overlap and at most 6 intervals per day. A day with no intervals is "Não atendo". Null when invalid.
    // A missing days list is invalid; an empty one is a version where every day is "Não atendo".
    public static Dictionary<int, List<Interval>>? ParseDays(IReadOnlyList<SaveAvailabilityDay?>? days)
    {
        if (days is null)
        {
            return null;
        }
        var result = new Dictionary<int, List<Interval>>();
        foreach (var day in days)
        {
            if (day?.Weekday is not (>= 0 and <= 6) || result.ContainsKey(day.Weekday.Value))
            {
                return null;
            }
            var intervals = new List<Interval>();
            foreach (var interval in day.Intervals ?? [])
            {
                if (ParseTime(interval?.Start) is not { } start || ParseTime(interval?.End) is not { } end || start >= end)
                {
                    return null;
                }
                intervals.Add(new Interval(start, end));
            }
            if (intervals.Count > MaxIntervalsPerDay)
            {
                return null;
            }
            intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (var i = 1; i < intervals.Count; i++)
            {
                if (intervals[i].Start < intervals[i - 1].End)
                {
                    return null;
                }
            }
            result[day.Weekday.Value] = intervals;
        }
        return result;
    }

    private static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            && time.Minute % StepMinutes == 0
            ? time
            : null;

    public static async Task<AvailabilityResponse> GetAsync(AnamnysDbContext db, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        var today = AvailabilityTime.LocalDate(now, AvailabilityTime.Provider);
        var headers = await db.AvailabilityVersions.AsNoTracking()
            .Where(v => v.ProviderId == providerId && (v.EffectiveTo == null || v.EffectiveTo >= today))
            .OrderBy(v => v.EffectiveFrom)
            .ToListAsync(ct);
        var ids = headers.Select(v => v.Id).ToList();
        var rows = await db.AvailabilityRules.AsNoTracking()
            .Where(r => r.ProviderId == providerId && ids.Contains(r.VersionId))
            .ToListAsync(ct);
        var versions = headers
            .Select(v => new AvailabilityVersion(v.EffectiveFrom, ToDays(rows.Where(r => r.VersionId == v.Id))))
            .ToList();
        var current = versions.LastOrDefault(v => v.EffectiveFrom <= today);
        return new AvailabilityResponse(current, [.. versions.Where(v => v.EffectiveFrom > today)]);
    }

    private static List<AvailabilityDay> ToDays(IEnumerable<AvailabilityRule> rows) =>
        [.. Enumerable.Range(0, 7).Select(weekday => new AvailabilityDay(weekday, [.. rows
            .Where(r => r.Weekday == weekday)
            .OrderBy(r => r.StartsAtTime)
            .Select(r => new AvailabilityInterval(Format(r.StartsAtTime), Format(r.EndsAtTime)))]))];

    private static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    // Saves a version from D: deletes the versions starting on or after D (their rules go with them), closes the version
    // open on D at D - 1 (header and rules), and writes the new header and its rules, open-ended. A version where every
    // day is "Não atendo" is a header with no rules.
    public static Task<AvailabilityOutcome> SaveAsync(
        AnamnysDbContext db, Guid providerId, SaveAvailabilityRequest? request, DateTimeOffset now, CancellationToken ct)
    {
        if (request?.EffectiveFrom is not { } from)
        {
            return Task.FromResult(AvailabilityOutcome.InvalidEffectiveFrom);
        }
        if (ParseDays(request.Days) is not { } days)
        {
            return Task.FromResult(AvailabilityOutcome.InvalidIntervals);
        }
        if (from < AvailabilityTime.LocalDate(now, AvailabilityTime.Provider))
        {
            return Task.FromResult(AvailabilityOutcome.EffectiveInPast);
        }

        return db.InTransactionAsync(async c =>
        {
            await LockProviderAsync(db, providerId, c);
            await db.AvailabilityRules
                .Where(r => r.ProviderId == providerId && r.EffectiveFrom >= from)
                .ExecuteDeleteAsync(c);
            await db.AvailabilityVersions
                .Where(v => v.ProviderId == providerId && v.EffectiveFrom >= from)
                .ExecuteDeleteAsync(c);
            await SetEndAsync(db, providerId, v => v.EffectiveFrom < from && (v.EffectiveTo == null || v.EffectiveTo >= from), from.AddDays(-1), c);

            var version = new AvailabilityRuleVersion
            {
                Id = Guid.NewGuid(),
                ProviderId = providerId,
                EffectiveFrom = from,
                EffectiveTo = null,
                CreatedAt = now.ToUniversalTime(),
            };
            db.AvailabilityVersions.Add(version);
            foreach (var (weekday, intervals) in days)
            {
                foreach (var interval in intervals)
                {
                    db.AvailabilityRules.Add(new AvailabilityRule
                    {
                        Id = Guid.NewGuid(),
                        ProviderId = providerId,
                        VersionId = version.Id,
                        Weekday = weekday,
                        StartsAtTime = interval.Start,
                        EndsAtTime = interval.End,
                        EffectiveFrom = from,
                        EffectiveTo = null,
                        Timezone = AvailabilityTime.ProviderZone,
                    });
                }
            }
            AddAudit(db, providerId, ChangedAction, AuditField, $"from {from:yyyy-MM-dd}", now);
            await db.SaveChangesAsync(c);
            return AvailabilityOutcome.Ok;
        }, ct);
    }

    // Cancels an upcoming version (also one with no rules): it goes, with its rules, and the version before it gets its
    // end back (open-ended, or up to the day before the version after it).
    public static Task<AvailabilityOutcome> CancelUpcomingAsync(
        AnamnysDbContext db, Guid providerId, DateOnly effectiveFrom, DateTimeOffset now, CancellationToken ct)
    {
        if (effectiveFrom <= AvailabilityTime.LocalDate(now, AvailabilityTime.Provider))
        {
            return Task.FromResult(AvailabilityOutcome.NotFound);
        }
        return db.InTransactionAsync(async c =>
        {
            await LockProviderAsync(db, providerId, c);
            var version = await db.AvailabilityVersions
                .SingleOrDefaultAsync(v => v.ProviderId == providerId && v.EffectiveFrom == effectiveFrom, c);
            if (version is null)
            {
                return AvailabilityOutcome.NotFound;
            }
            await db.AvailabilityRules.Where(r => r.VersionId == version.Id).ExecuteDeleteAsync(c);
            await db.AvailabilityVersions.Where(v => v.Id == version.Id).ExecuteDeleteAsync(c);
            var dayBefore = effectiveFrom.AddDays(-1);
            await SetEndAsync(db, providerId, v => v.EffectiveTo == dayBefore, version.EffectiveTo, c);
            db.ChangeTracker.Clear();
            AddAudit(db, providerId, ChangedAction, AuditField, $"cancel {effectiveFrom:yyyy-MM-dd}", now);
            await db.SaveChangesAsync(c);
            return AvailabilityOutcome.Ok;
        }, ct);
    }

    // Sets EffectiveTo on the matching version headers and on their rules, which carry the same dates.
    private static async Task SetEndAsync(
        AnamnysDbContext db, Guid providerId, System.Linq.Expressions.Expression<Func<AvailabilityRuleVersion, bool>> which,
        DateOnly? end, CancellationToken ct)
    {
        var ids = await db.AvailabilityVersions.Where(v => v.ProviderId == providerId).Where(which).Select(v => v.Id).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return;
        }
        await db.AvailabilityVersions.Where(v => ids.Contains(v.Id)).ExecuteUpdateAsync(s => s.SetProperty(v => v.EffectiveTo, end), ct);
        await db.AvailabilityRules.Where(r => ids.Contains(r.VersionId)).ExecuteUpdateAsync(s => s.SetProperty(r => r.EffectiveTo, end), ct);
    }

    // Whether the provider has a version (even one with no rules) in force on or before a date: before that, they had no
    // saved hours, so nothing is outside them.
    internal static Task<bool> HasHoursByAsync(AnamnysDbContext db, Guid providerId, DateOnly date, CancellationToken ct) =>
        db.AvailabilityVersions.AnyAsync(v => v.ProviderId == providerId && v.EffectiveFrom <= date, ct);

    // Serialises the availability and block writes of one provider.
    internal static Task LockProviderAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Providers\" WHERE \"Id\" = {providerId} FOR UPDATE", ct);

    public static async Task<HolidaySettingsResponse> GetHolidaySettingsAsync(
        AnamnysDbContext db, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        var settings = await db.ProviderHolidaySettings.AsNoTracking().SingleOrDefaultAsync(s => s.ProviderId == providerId, ct);
        var year = AvailabilityTime.LocalDate(now, AvailabilityTime.Provider).Year;
        var holidays = NationalHolidays.For(year).Concat(NationalHolidays.For(year + 1))
            .Select(h => new HolidayItem(h.Key, h.Name, h.Date))
            .ToList();
        return new HolidaySettingsResponse(settings?.WorksOnHolidays ?? false, settings?.DisabledHolidays ?? [], holidays);
    }

    public static async Task<AvailabilityOutcome> SaveHolidaySettingsAsync(
        AnamnysDbContext db, Guid providerId, SaveHolidaySettingsRequest? request, DateTimeOffset now, CancellationToken ct)
    {
        if (request?.WorksOnHolidays is not { } works
            || (request.DisabledHolidays ?? []).Any(k => k is null || !NationalHolidays.Keys.Contains(k)))
        {
            return AvailabilityOutcome.InvalidHolidays;
        }
        var disabled = (request.DisabledHolidays ?? []).Select(k => k!).Distinct().Order(StringComparer.Ordinal).ToArray();
        return await db.InTransactionAsync(async c =>
        {
            await LockProviderAsync(db, providerId, c);
            var settings = await db.ProviderHolidaySettings.SingleOrDefaultAsync(s => s.ProviderId == providerId, c);
            if (settings is null)
            {
                settings = new ProviderHolidaySettings { ProviderId = providerId };
                db.ProviderHolidaySettings.Add(settings);
            }
            settings.WorksOnHolidays = works;
            settings.DisabledHolidays = disabled;
            AddAudit(db, providerId, ChangedAction, "holidays", $"works {(works ? "yes" : "no")}; off {string.Join(",", disabled)}", now);
            await db.SaveChangesAsync(c);
            return AvailabilityOutcome.Ok;
        }, ct);
    }

    internal static void AddAudit(AnamnysDbContext db, Guid providerId, string action, string field, string value, DateTimeOffset at) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            ActorId = providerId,
            Action = action,
            FieldChanged = field,
            NewValue = value,
            At = at.ToUniversalTime(),
        });
}
