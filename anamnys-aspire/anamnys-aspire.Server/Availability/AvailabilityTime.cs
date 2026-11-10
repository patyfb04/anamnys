using System.Collections.Concurrent;

namespace Anamnys.Server.Availability;

// Local dates and times of the provider's zone (design/specs/2026-11-09-availability-design.md §3), the same mechanism
// as the appointments: an IANA id stored per row (America/Sao_Paulo today) resolved with TimeZoneInfo.
public static class AvailabilityTime
{
    // The provider's zone. One zone per practice today: the appointments' zone.
    public const string ProviderZone = Appointments.Appointments.PracticeTimezone;

    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Zones = new();

    public static TimeZoneInfo Zone(string id) => Zones.GetOrAdd(id, TimeZoneInfo.FindSystemTimeZoneById);

    public static TimeZoneInfo Provider => Zone(ProviderZone);

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    // A local wall-clock time as an instant, DST-safe. A time skipped by a spring-forward gap takes the offset in force
    // before the gap, which lands on the first instant after it (a skipped midnight becomes 01:00); a time that happens
    // twice takes its first occurrence.
    public static DateTimeOffset ToInstant(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var offset = zone.IsInvalidTime(local)
            ? zone.GetUtcOffset(local.AddHours(-3))
            : zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    // Local midnight of a date: the first instant of that local day.
    public static DateTimeOffset DayStart(DateOnly date, TimeZoneInfo zone) => ToInstant(date, TimeOnly.MinValue, zone);
}
