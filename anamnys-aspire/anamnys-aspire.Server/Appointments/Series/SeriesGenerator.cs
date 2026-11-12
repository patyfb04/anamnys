using System.Globalization;
using Anamnys.Server.Availability;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Appointments.Series;

// The pattern of a series (design/specs/2026-11-09-appointment-series-design.md §3): occurrence k (1-based) falls on
// StartsOn + (k - 1) * 7 * Interval days, at StartTime in Timezone (DST-safe: the local time is kept).
public static class SeriesPattern
{
    public const int HorizonDays = 84;
    public const int MaxOccurrences = 520;

    private static readonly string[] DayNames = ["domingo", "segunda", "terça", "quarta", "quinta", "sexta", "sábado"];
    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    public static DateOnly DateOf(AppointmentSeries series, int index) =>
        series.StartsOn.AddDays((index - 1) * 7 * series.Interval);

    public static DateTimeOffset StartOf(AppointmentSeries series, int index) =>
        AvailabilityTime.ToInstant(DateOf(series, index), series.StartTime, AvailabilityTime.Zone(series.Timezone));

    // The last local date the series covers: its end date, the date of its last session, or null when it never ends.
    public static DateOnly? LastDate(AppointmentSeries series) =>
        series.EndsOn ?? (series.OccurrenceCount is { } count ? DateOf(series, count) : null);

    public static bool IsActive(AppointmentSeries series, DateOnly today) =>
        series.CancelledAt is null && (LastDate(series) is not { } last || last >= today);

    public static int WeekdayOf(DateOnly date) => (int)date.DayOfWeek;

    // The readable rule kept in RecurrenceRule, e.g. WEEKLY;INTERVAL=2;BYDAY=TU.
    public static string Rule(int interval, int weekday) =>
        $"WEEKLY;INTERVAL={interval};BYDAY={DayCodes[weekday]}";

    // "toda terça às 14:00", "todo sábado às 09:00", "terça sim, terça não, às 14:00".
    public static string Describe(AppointmentSeries series) => Describe(series.Interval, series.Weekday, series.StartTime);

    public static string Describe(int interval, int weekday, TimeOnly startTime)
    {
        var day = DayNames[weekday];
        var time = startTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (interval == 2)
        {
            return $"{day} sim, {day} não, às {time}";
        }
        var every = weekday is 0 or 6 ? "todo" : "toda";
        return $"{every} {day} às {time}";
    }

    // "toda terça às 14:00, a partir de 17/11, 10 sessões" (or "..., até 15/12" / no end).
    public static string Summary(AppointmentSeries series)
    {
        var text = $"{Describe(series)}, a partir de {ShortDate(series.StartsOn, series.StartsOn.Year)}";
        if (series.OccurrenceCount is { } count)
        {
            text += count == 1 ? ", 1 sessão" : $", {count} sessões";
        }
        else if (series.EndsOn is { } endsOn)
        {
            text += $", até {ShortDate(endsOn, series.StartsOn.Year)}";
        }
        return text;
    }

    public static string ShortDate(DateOnly date, int referenceYear) =>
        date.ToString(date.Year == referenceYear ? "dd/MM" : "dd/MM/yyyy", CultureInfo.InvariantCulture);

    public static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
}

// One occurrence generation left out because its slot was taken by another active appointment of the provider.
public sealed record SkippedOccurrence(int SeriesIndex, DateTimeOffset StartsAt);

// What one generation pass did: the occurrences it created and the ones it skipped (never silently: create and split
// return them, the daily extension turns them into bell notices).
public sealed record SeriesGeneration(IReadOnlyList<Appointment> Created, IReadOnlyList<SkippedOccurrence> Skipped)
{
    public static readonly SeriesGeneration None = new([], []);
}

// Generation of a series' occurrences (design/specs/2026-11-09-appointment-series-design.md §3 Generation). Creates the
// missing occurrences with a local date up to today + 84 days, stopping at EndsOn or OccurrenceCount; dates already
// covered by GeneratedUntil are not visited again, and an index that already exists is skipped silently (it is there).
// An occurrence whose slot is taken by another active appointment of the provider is not created (the overlap
// constraint would refuse it) and is reported in Skipped. Occurrences on blocks or holidays are still created. An
// occurrence whose start is not after now is never created and not reported (GeneratedUntil still moves past it).
// Generation never cancels anything and never saves.
public static class SeriesGenerator
{
    public static async Task<SeriesGeneration> ExtendAsync(
        AnamnysDbContext db, AppointmentSeries series, DateOnly today, CancellationToken ct, DateTimeOffset? now = null)
    {
        if (series.CancelledAt is not null)
        {
            return SeriesGeneration.None;
        }
        var nowUtc = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var last = today.AddDays(SeriesPattern.HorizonDays);
        if (SeriesPattern.LastDate(series) is { } end && end < last)
        {
            last = end;
        }

        var step = 7 * series.Interval;
        var first = series.GeneratedUntil is { } covered && covered >= series.StartsOn
            ? (covered.DayNumber - series.StartsOn.DayNumber) / step + 2
            : 1;
        var candidates = new List<(int Index, DateTimeOffset StartsAt)>();
        for (var index = first; index <= SeriesPattern.MaxOccurrences; index++)
        {
            if (SeriesPattern.DateOf(series, index) > last || index > (series.OccurrenceCount ?? int.MaxValue))
            {
                break;
            }
            var startsAt = SeriesPattern.StartOf(series, index);
            // A date already past (a series resumed after a long pause, e.g. an archived patient or a stopped worker) is
            // passed over silently: it is not a taken slot, so it is neither created nor reported.
            if (startsAt > nowUtc)
            {
                candidates.Add((index, startsAt));
            }
        }

        var created = new List<Appointment>();
        var skipped = new List<SkippedOccurrence>();
        if (candidates.Count > 0)
        {
            var duration = TimeSpan.FromMinutes(series.DefaultMinutes);
            var existing = (await db.Appointments.AsNoTracking()
                    .Where(a => a.SeriesId == series.Id && a.SeriesIndex != null)
                    .Select(a => a.SeriesIndex!.Value)
                    .ToListAsync(ct))
                .Concat(db.ChangeTracker.Entries<Appointment>()
                    .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Unchanged
                        && e.Entity.SeriesId == series.Id && e.Entity.SeriesIndex != null)
                    .Select(e => e.Entity.SeriesIndex!.Value))
                .ToHashSet();
            var from = candidates[0].StartsAt;
            var to = candidates[^1].StartsAt + duration;
            var busy = await db.Appointments.AsNoTracking()
                .Where(a => a.ProviderId == series.ProviderId && a.Status != AppointmentStatus.Cancelled
                    && a.StartsAt < to && a.EndsAt > from)
                .Select(a => new { a.StartsAt, a.EndsAt })
                .ToListAsync(ct);
            var hasEmail = await db.Patients.AsNoTracking()
                .AnyAsync(p => p.Id == series.PatientId && p.ProviderId == series.ProviderId && p.ContactEmail != null, ct);
            var lead = await ConfirmationLeadAsync(db, series.ProviderId, ct);

            foreach (var (index, startsAt) in candidates)
            {
                var endsAt = startsAt + duration;
                if (existing.Contains(index))
                {
                    continue;
                }
                if (busy.Any(b => b.StartsAt < endsAt && b.EndsAt > startsAt))
                {
                    skipped.Add(new SkippedOccurrence(index, startsAt));
                    continue;
                }
                var appointment = new Appointment
                {
                    Id = Guid.NewGuid(),
                    ProviderId = series.ProviderId,
                    PatientId = series.PatientId,
                    SeriesId = series.Id,
                    SeriesIndex = index,
                    Detached = false,
                    StartsAt = startsAt,
                    EndsAt = endsAt,
                    Timezone = series.Timezone,
                    Modality = series.DefaultModality,
                    Status = AppointmentStatus.Scheduled,
                    CreatedBy = "provider",
                    CreatedAt = nowUtc,
                };
                db.Appointments.Add(appointment);
                AppointmentNotifications.OnSeriesOccurrence(db, appointment, hasEmail, nowUtc, lead);
                created.Add(appointment);
            }
        }

        if (last >= series.StartsOn && (series.GeneratedUntil is null || series.GeneratedUntil < last))
        {
            series.GeneratedUntil = last;
        }
        return new SeriesGeneration(created, skipped);
    }

    // How long before a series occurrence its confirmation request goes out: 48 h, or the provider's auto-cancel hours
    // plus 24 h when that is longer, so an auto-cancel deadline always falls after the request and before the session in
    // both modes (design/specs/2026-11-09-appointment-series-design.md §3 Notices). With auto-cancel off, 48 h.
    public static async Task<TimeSpan> ConfirmationLeadAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct)
    {
        var policy = await db.BookingPolicies.AsNoTracking()
            .Where(b => b.ProviderId == providerId)
            .Select(b => new { b.AutoCancelMode, b.AutoCancelHours })
            .SingleOrDefaultAsync(ct);
        var mode = policy?.AutoCancelMode ?? ConfirmationRules.DefaultMode;
        var hours = policy?.AutoCancelHours ?? ConfirmationRules.DefaultHours;
        return ConfirmationRules.SeriesConfirmationLeadFor(mode, hours);
    }
}