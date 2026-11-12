using Anamnys.Server.Availability;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Appointments.Series;

// The daily extension (design/specs/2026-11-09-appointment-series-design.md §3): at start and then once a day, every
// series that is not cancelled, not ended and not yet generated to its end is extended to today + 84 days. Extension
// sends no summary e-mail (each new occurrence gets only its confirmation at StartsAt - 48 h and the 1 h reminder) and
// never cancels anything. Logs counts only.
public sealed class SeriesExtensionJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SeriesExtensionJob> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
                var (series, created, skipped, failed) = await RunOnceAsync(db, clock.GetUtcNow(), stoppingToken, logger: logger);
                logger.LogInformation(
                    "Series extension: {Series} series extended, {Created} occurrences created, {Skipped} skipped, {Failed} failed.",
                    series, created, skipped, failed);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // Type only: the exception message could carry row data.
                logger.LogError("Series extension failed: {ExceptionType}", e.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // One pass. Each series is extended in its own transaction with its row locked, so a failure (an archived or
    // discarded record, a race on a slot) affects that series only and is retried on the next pass. providerId
    // narrows a pass to one provider (tests); production passes null. An occurrence skipped because its slot was taken
    // becomes one in-app bell notice for the provider (kind series_occurrence_skipped, subject the series), at most once
    // per series and index.
    public static async Task<(int Series, int Created, int Skipped, int Failed)> RunOnceAsync(
        AnamnysDbContext db, DateTimeOffset now, CancellationToken ct, Guid? providerId = null, ILogger? logger = null)
    {
        var nowUtc = now.ToUniversalTime();
        var today = AvailabilityTime.LocalDate(nowUtc, AvailabilityTime.Provider);
        var horizon = today.AddDays(SeriesPattern.HorizonDays);
        db.ChangeTracker.Clear();
        var candidates = await (
                from s in db.AppointmentSeries.AsNoTracking()
                join p in db.Patients.AsNoTracking() on new { Id = s.PatientId, s.ProviderId } equals new { p.Id, p.ProviderId }
                where s.CancelledAt == null && p.ArchivedAt == null && p.DiscardedAt == null
                    && (providerId == null || s.ProviderId == providerId)
                    && (s.GeneratedUntil == null || s.GeneratedUntil < horizon)
                    && (s.EndsOn == null || s.GeneratedUntil == null || s.EndsOn > s.GeneratedUntil)
                select s)
            .ToListAsync(ct);
        var due = candidates
            .Where(s => SeriesPattern.LastDate(s) is not { } last || s.GeneratedUntil is not { } covered || last > covered)
            .Select(s => s.Id)
            .ToList();

        var extended = 0;
        var created = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var id in due)
        {
            try
            {
                var generation = await db.InTransactionAsync(async c =>
                {
                    await SeriesService.LockSeriesAsync(db, id, c);
                    var series = await db.AppointmentSeries.SingleAsync(s => s.Id == id, c);
                    var result = await SeriesGenerator.ExtendAsync(db, series, today, c, nowUtc);
                    await NotifySkippedAsync(db, series, result.Skipped, nowUtc, c);
                    await db.SaveChangesAsync(c);
                    return result;
                }, ct);
                extended++;
                created += generation.Created.Count;
                skipped += generation.Skipped.Count;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                failed++;
                // Type only: no ids, names or messages.
                logger?.LogWarning("A series extension failed: {ExceptionType}.", e.GetType().Name);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }
        return (extended, created, skipped, failed);
    }

    private static async Task NotifySkippedAsync(
        AnamnysDbContext db, AppointmentSeries series, IReadOnlyList<SkippedOccurrence> skipped, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (skipped.Count == 0)
        {
            return;
        }
        var keys = skipped.Select(s => NotificationKinds.SkippedDedupeKey(series.Id, s.SeriesIndex)).ToList();
        var existing = await db.Notifications.AsNoTracking()
            .Where(n => n.ProviderId == series.ProviderId && n.DedupeKey != null && keys.Contains(n.DedupeKey))
            .Select(n => n.DedupeKey!)
            .ToListAsync(ct);
        foreach (var key in keys.Except(existing))
        {
            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                ProviderId = series.ProviderId,
                Kind = NotificationKinds.SeriesOccurrenceSkipped,
                SubjectType = NotificationKinds.SeriesSubject,
                SubjectId = series.Id,
                Channel = "in_app",
                ScheduledFor = nowUtc,
                SentAt = nowUtc,
                DeliveryStatus = "sent",
                DedupeKey = key,
            });
        }
    }
}
