using Anamnys.Server.Availability;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notifications;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.Appointments.Series;

public enum SeriesOutcome
{
    Ok, NotFound, PatientArchived, InvalidInterval, InvalidEnd, InvalidOccurrence, InvalidDate, StartInPast, SeriesEnded, Overlap, OutsideAvailability,
}

public sealed record SeriesResult<T>(SeriesOutcome Outcome, T? Value = default, IReadOnlyList<OutsideAvailabilityReason>? Reasons = null);

// Series operations (design/specs/2026-11-09-appointment-series-design.md §3, §4). Every call resolves the series (or
// patient, or occurrence) with ProviderId == providerId, so a foreign id is indistinguishable from a missing one. Audit
// values carry ids, dates and counts only, never names.
public static class SeriesService
{
    public const string CreatedAction = "series_created";
    public const string SplitAction = "series_split";
    public const string EndedAction = "series_ended";
    public const string AuditField = "series";
    public const string EndCancellationReason = "série encerrada";
    private const string OverlapConstraint = "Appointments_no_overlap";

    private static readonly string[] Active = [AppointmentStatus.Scheduled, AppointmentStatus.Confirmed];

    // ---- Create ----

    public static async Task<SeriesResult<SeriesCreatedResponse>> CreateAsync(
        AnamnysDbContext db, Guid providerId, CreateSeriesRequest request, DateTimeOffset now, CancellationToken ct)
    {
        if (request.Interval is not (1 or 2))
        {
            return new(SeriesOutcome.InvalidInterval);
        }
        var nowUtc = now.ToUniversalTime();
        var zone = AvailabilityTime.Provider;
        var startsAt = request.StartsAt!.Value.ToUniversalTime();
        if (startsAt <= nowUtc)
        {
            // A series never starts in the past: generation would create sessions that already happened.
            return new(SeriesOutcome.StartInPast);
        }
        var local = TimeZoneInfo.ConvertTime(startsAt, zone);
        var startsOn = DateOnly.FromDateTime(local.DateTime);
        if ((request.EndsOn is not null && request.OccurrenceCount is not null)
            || request.EndsOn < startsOn
            || request.OccurrenceCount is < 1 or > SeriesPattern.MaxOccurrences)
        {
            return new(SeriesOutcome.InvalidEnd);
        }

        var patient = await db.Patients.AsNoTracking()
            .Where(p => p.Id == request.PatientId && p.ProviderId == providerId)
            .Select(p => new { p.ArchivedAt, p.ContactEmail })
            .SingleOrDefaultAsync(ct);
        if (patient is null)
        {
            return new(SeriesOutcome.NotFound);
        }
        if (patient.ArchivedAt is not null)
        {
            return new(SeriesOutcome.PatientArchived);
        }

        var endsAt = startsAt.AddMinutes(request.DurationMinutes!.Value);
        var reasons = await AvailabilityCheck.ReasonsAsync(db, providerId, startsAt, endsAt, ct);
        if (reasons.Count > 0 && !request.AcknowledgeOutsideAvailability)
        {
            return new(SeriesOutcome.OutsideAvailability, Reasons: reasons);
        }

        var weekday = SeriesPattern.WeekdayOf(startsOn);
        var series = new AppointmentSeries
        {
            Id = Guid.NewGuid(),
            PatientId = request.PatientId!.Value,
            ProviderId = providerId,
            RecurrenceRule = SeriesPattern.Rule(request.Interval.Value, weekday),
            StartsOn = startsOn,
            EndsOn = request.EndsOn,
            OccurrenceCount = request.OccurrenceCount,
            DefaultMinutes = request.DurationMinutes.Value,
            DefaultModality = request.Modality!,
            Interval = request.Interval.Value,
            Weekday = weekday,
            StartTime = TimeOnly.FromDateTime(local.DateTime),
            Timezone = AvailabilityTime.ProviderZone,
            CreatedAt = nowUtc,
        };
        db.AppointmentSeries.Add(series);

        // A first session beyond the horizon is still generated now: the horizon reaches at least the first date.
        var today = AvailabilityTime.LocalDate(nowUtc, zone);
        var generationDay = Max(today, startsOn.AddDays(-SeriesPattern.HorizonDays));
        var generation = await SeriesGenerator.ExtendAsync(db, series, generationDay, ct, nowUtc);
        var created = generation.Created;
        var first = created.FirstOrDefault(a => a.SeriesIndex == 1);
        if (first is null)
        {
            // The first session's slot is taken: nothing is written.
            db.ChangeTracker.Clear();
            return new(SeriesOutcome.Overlap);
        }

        if (patient.ContactEmail is not null)
        {
            AppointmentNotifications.QueueSeriesNotice(db, first.Id, ReminderTemplates.SeriesScheduled, nowUtc);
        }
        if (reasons.Count > 0)
        {
            AvailabilityCheck.AddAudit(db, providerId, first.Id, reasons, nowUtc);
        }
        AvailabilityRules.AddAudit(db, providerId, CreatedAction, AuditField,
            $"{series.Id} {series.RecurrenceRule} from {series.StartsOn:yyyy-MM-dd} generated {created.Count} skipped {generation.Skipped.Count}", nowUtc);

        var saved = await Appointments.SaveAsync(db, ct);
        return saved == AppointmentOutcome.Ok
            ? new(SeriesOutcome.Ok, new SeriesCreatedResponse(series.Id, first.Id, created.Count, generation.Skipped))
            : new(SeriesOutcome.Overlap);
    }

    // ---- Read ----

    public static async Task<SeriesResponse?> GetAsync(
        AnamnysDbContext db, Guid providerId, Guid seriesId, DateTimeOffset now, CancellationToken ct)
    {
        var series = await db.AppointmentSeries.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == seriesId && s.ProviderId == providerId, ct);
        if (series is null)
        {
            return null;
        }
        var nowUtc = now.ToUniversalTime();
        var upcoming = await db.Appointments.AsNoTracking()
            .Where(a => a.SeriesId == series.Id && a.ProviderId == providerId && a.EndsAt > nowUtc)
            .OrderBy(a => a.StartsAt).ThenBy(a => a.Id)
            .Select(a => new SeriesOccurrenceItem(a.Id, a.StartsAt, a.Status, a.SeriesIndex, a.Detached))
            .ToListAsync(ct);
        var s = Summary(series, AvailabilityTime.LocalDate(nowUtc, AvailabilityTime.Provider));
        return new SeriesResponse(s.Id, s.PatientId, s.Interval, s.Weekday, s.StartTime, s.Timezone, s.StartsOn, s.EndsOn,
            s.OccurrenceCount, s.Active, s.Description, upcoming);
    }

    // The patient's active series, oldest first; null when the patient is not the provider's.
    public static async Task<IReadOnlyList<SeriesSummaryResponse>?> ListForPatientAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        if (!await db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId && p.ProviderId == providerId, ct))
        {
            return null;
        }
        var today = AvailabilityTime.LocalDate(now.ToUniversalTime(), AvailabilityTime.Provider);
        var rows = await db.AppointmentSeries.AsNoTracking()
            .Where(s => s.PatientId == patientId && s.ProviderId == providerId && s.CancelledAt == null)
            .OrderBy(s => s.StartsOn).ThenBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .ToListAsync(ct);
        return [.. rows.Where(s => SeriesPattern.IsActive(s, today)).Select(s => Summary(s, today))];
    }

    public static SeriesSummaryResponse Summary(AppointmentSeries s, DateOnly today) => new(
        s.Id, s.PatientId, s.Interval, s.Weekday, s.StartTime.ToString("HH:mm"), s.Timezone, s.StartsOn, s.EndsOn, s.OccurrenceCount,
        SeriesPattern.IsActive(s, today), SeriesPattern.Describe(s));

    // ---- Split ("this and following") ----

    private sealed record SplitPlan(
        AppointmentSeries Series, int FromIndex, DateOnly NewStartsOn, TimeOnly NewTime, DateTimeOffset NewFirstStartsAt,
        int? NewFirstIndex, List<Appointment> Moving, List<(Appointment Appointment, string Reason)> Staying, List<Appointment> Relinked);

    public static async Task<SeriesResult<SplitPreviewResponse>> PreviewSplitAsync(
        AnamnysDbContext db, Guid providerId, Guid seriesId, SplitSeriesRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var (outcome, plan) = await PlanSplitAsync(db, providerId, seriesId, request, now, tracked: false, ct);
        return plan is null
            ? new(outcome)
            : new(SeriesOutcome.Ok, new SplitPreviewResponse(
                plan.NewFirstStartsAt,
                plan.Moving.Count,
                [.. plan.Staying.Select(s => new SplitStayItem(s.Appointment.Id, s.Appointment.StartsAt, s.Reason))]));
    }

    public static async Task<SeriesResult<SplitResponse>> SplitAsync(
        AnamnysDbContext db, Guid providerId, Guid seriesId, SplitSeriesRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        try
        {
            return await db.InTransactionAsync(async c =>
            {
                await LockSeriesAsync(db, seriesId, c);
                var (outcome, plan) = await PlanSplitAsync(db, providerId, seriesId, request, nowUtc, tracked: true, c);
                if (plan is null)
                {
                    return new SeriesResult<SplitResponse>(outcome);
                }

                // Availability and the slot are checked on the first session the new series actually generates (not on
                // the pattern date of an occurrence that stays); when it generates none, there is no slot to check.
                var duration = TimeSpan.FromMinutes(request.DurationMinutes!.Value);
                var newEndsAt = plan.NewFirstStartsAt + duration;
                var reasons = plan.NewFirstIndex is null
                    ? []
                    : await AvailabilityCheck.ReasonsAsync(db, providerId, plan.NewFirstStartsAt, newEndsAt, c);
                if (reasons.Count > 0 && !request.AcknowledgeOutsideAvailability)
                {
                    return new SeriesResult<SplitResponse>(SeriesOutcome.OutsideAvailability, Reasons: reasons);
                }
                var movingIds = plan.Moving.Select(a => a.Id).ToList();
                if (plan.NewFirstIndex is not null && await db.Appointments.AnyAsync(a => a.ProviderId == providerId
                        && a.Status != AppointmentStatus.Cancelled && !movingIds.Contains(a.Id)
                        && a.StartsAt < newEndsAt && a.EndsAt > plan.NewFirstStartsAt, c))
                {
                    return new SeriesResult<SplitResponse>(SeriesOutcome.Overlap);
                }

                var old = plan.Series;
                var shift = plan.FromIndex - 1;
                var next = new AppointmentSeries
                {
                    Id = Guid.NewGuid(),
                    PatientId = old.PatientId,
                    ProviderId = providerId,
                    RecurrenceRule = SeriesPattern.Rule(old.Interval, request.Weekday!.Value),
                    StartsOn = plan.NewStartsOn,
                    EndsOn = old.EndsOn,
                    OccurrenceCount = old.OccurrenceCount is { } count ? count - shift : null,
                    DefaultMinutes = request.DurationMinutes.Value,
                    DefaultModality = request.Modality!,
                    Interval = old.Interval,
                    Weekday = request.Weekday.Value,
                    StartTime = plan.NewTime,
                    Timezone = old.Timezone,
                    PreviousSeriesId = old.Id,
                    CreatedAt = nowUtc,
                };

                // The old series ends the day before occurrence k; split at the first session, it is cancelled instead.
                if (plan.FromIndex == 1)
                {
                    old.CancelledAt = nowUtc;
                }
                else
                {
                    old.EndsOn = SeriesPattern.DateOf(old, plan.FromIndex).AddDays(-1);
                    old.OccurrenceCount = null;
                }
                db.AppointmentSeries.Add(next);
                // Every occurrence from k that does not move is re-linked under its new number, so the new series never
                // generates its week again (a cancelled week stays cancelled).
                foreach (var appointment in plan.Relinked)
                {
                    appointment.SeriesId = next.Id;
                    appointment.SeriesIndex = appointment.SeriesIndex - shift;
                    appointment.Detached = true;
                }
                db.Appointments.RemoveRange(plan.Moving);
                await db.SaveChangesAsync(c);

                // Regenerated by the new series; the staying sessions keep their numbers there and are skipped.
                var today = AvailabilityTime.LocalDate(nowUtc, AvailabilityTime.Zone(next.Timezone));
                var generation = await SeriesGenerator.ExtendAsync(db, next, Max(today, next.StartsOn.AddDays(-SeriesPattern.HorizonDays)), c, nowUtc);
                var created = generation.Created;
                var hasEmail = await db.Patients.AsNoTracking()
                    .AnyAsync(p => p.Id == next.PatientId && p.ProviderId == providerId && p.ContactEmail != null, c);
                var noticeOn = created.Select(a => (a.SeriesIndex, a.Id))
                    .Concat(plan.Staying.Select(s => (s.Appointment.SeriesIndex, s.Appointment.Id)))
                    .OrderBy(x => x.SeriesIndex)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefault();
                if (hasEmail && noticeOn is { } appointmentId)
                {
                    AppointmentNotifications.QueueSeriesNotice(db, appointmentId, ReminderTemplates.SeriesScheduled, nowUtc);
                }
                if (reasons.Count > 0 && created.FirstOrDefault(a => a.SeriesIndex == plan.NewFirstIndex) is { } newFirst)
                {
                    AvailabilityCheck.AddAudit(db, providerId, newFirst.Id, reasons, nowUtc);
                }
                AvailabilityRules.AddAudit(db, providerId, SplitAction, AuditField,
                    $"{old.Id} -> {next.Id} from {plan.FromIndex} moved {plan.Moving.Count} kept {plan.Staying.Count} relinked {plan.Relinked.Count} skipped {generation.Skipped.Count}", nowUtc);
                await db.SaveChangesAsync(c);
                return new SeriesResult<SplitResponse>(SeriesOutcome.Ok, new SplitResponse(next.Id, generation.Skipped));
            }, ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { ConstraintName: OverlapConstraint })
        {
            // A session was booked into the new pattern's slot meanwhile: the whole split was rolled back.
            db.ChangeTracker.Clear();
            return new(SeriesOutcome.Overlap);
        }
    }

    // What a split from the given occurrence does. The occurrence must belong to the series, be scheduled or confirmed
    // and lie ahead. Moving: occurrences from k on that are scheduled and never changed on their own (deleted and
    // regenerated). Relinked: every other occurrence from k on (detached, confirmed, cancelled, attended, no-show),
    // moved to the new series under index oldIndex - (k - 1) and marked detached. Staying (what the preview lists): the
    // relinked ones that are still scheduled or confirmed, i.e. the sessions that keep their time.
    private static async Task<(SeriesOutcome, SplitPlan?)> PlanSplitAsync(
        AnamnysDbContext db, Guid providerId, Guid seriesId, SplitSeriesRequest request, DateTimeOffset now, bool tracked, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        var seriesQuery = tracked ? db.AppointmentSeries : db.AppointmentSeries.AsNoTracking();
        var series = await seriesQuery.SingleOrDefaultAsync(s => s.Id == seriesId && s.ProviderId == providerId, ct);
        if (series is null)
        {
            return (SeriesOutcome.NotFound, null);
        }
        var from = await db.Appointments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == request.FromAppointmentId && a.ProviderId == providerId && a.SeriesId == series.Id, ct);
        if (from is null)
        {
            return (SeriesOutcome.NotFound, null);
        }
        var today = AvailabilityTime.LocalDate(nowUtc, AvailabilityTime.Zone(series.Timezone));
        if (!SeriesPattern.IsActive(series, today))
        {
            return (SeriesOutcome.SeriesEnded, null);
        }
        if (from.SeriesIndex is not { } k || !Active.Contains(from.Status) || from.StartsAt <= nowUtc)
        {
            return (SeriesOutcome.InvalidOccurrence, null);
        }

        var time = SeriesPattern.ParseTime(request.StartTime)!.Value;
        var occurrenceDate = SeriesPattern.DateOf(series, k);
        var newStartsOn = occurrenceDate.AddDays((request.Weekday!.Value - SeriesPattern.WeekdayOf(occurrenceDate) + 7) % 7);
        var newFirst = AvailabilityTime.ToInstant(newStartsOn, time, AvailabilityTime.Zone(series.Timezone));
        if (newFirst <= nowUtc)
        {
            return (SeriesOutcome.InvalidOccurrence, null);
        }
        if (series.EndsOn is { } endsOn && endsOn < newStartsOn)
        {
            return (SeriesOutcome.InvalidEnd, null);
        }

        var appointments = tracked ? db.Appointments : db.Appointments.AsNoTracking();
        var following = await appointments
            .Where(a => a.SeriesId == series.Id && a.ProviderId == providerId && a.SeriesIndex >= k)
            .OrderBy(a => a.SeriesIndex).ThenBy(a => a.StartsAt)
            .ToListAsync(ct);
        var moving = following.Where(a => a.Status == AppointmentStatus.Scheduled && !a.Detached).ToList();
        var relinked = following.Except(moving).ToList();
        var staying = relinked
            .Where(a => Active.Contains(a.Status))
            .OrderBy(a => a.StartsAt).ThenBy(a => a.Id)
            .Select(a => (a, a.Detached ? StayReasons.Detached : StayReasons.Confirmed))
            .ToList();

        // The first session the new series will actually generate: the first new index not taken by a re-linked
        // occurrence (when occurrence k stays, its new-pattern date never exists). Null when every index up to the end
        // is taken; the pattern date of k is then reported and no slot is checked.
        var keptIndexes = relinked.Select(a => a.SeriesIndex!.Value - (k - 1)).ToHashSet();
        int? newCount = series.OccurrenceCount is { } count ? count - (k - 1) : null;
        int? firstIndex = null;
        var firstStartsAt = newFirst;
        for (var j = 1; j <= SeriesPattern.MaxOccurrences; j++)
        {
            var date = newStartsOn.AddDays((j - 1) * 7 * series.Interval);
            if ((series.EndsOn is { } last && date > last) || j > (newCount ?? int.MaxValue))
            {
                break;
            }
            if (!keptIndexes.Contains(j))
            {
                firstIndex = j;
                firstStartsAt = AvailabilityTime.ToInstant(date, time, AvailabilityTime.Zone(series.Timezone));
                break;
            }
        }
        return (SeriesOutcome.Ok, new SplitPlan(series, k, newStartsOn, time, firstStartsAt, firstIndex, moving, staying, relinked));
    }

    // ---- End ----

    public static async Task<SeriesResult<EndSeriesResponse>> EndAsync(
        AnamnysDbContext db, Guid providerId, Guid seriesId, EndSeriesRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        return await db.InTransactionAsync(async c =>
        {
            await LockSeriesAsync(db, seriesId, c);
            var series = await db.AppointmentSeries.SingleOrDefaultAsync(s => s.Id == seriesId && s.ProviderId == providerId, c);
            if (series is null)
            {
                return new SeriesResult<EndSeriesResponse>(SeriesOutcome.NotFound);
            }
            var zone = AvailabilityTime.Zone(series.Timezone);
            var today = AvailabilityTime.LocalDate(nowUtc, zone);
            if (request.FromDate is not { } fromDate || fromDate < today)
            {
                return new SeriesResult<EndSeriesResponse>(SeriesOutcome.InvalidDate);
            }
            if (series.CancelledAt is not null || SeriesPattern.LastDate(series) is { } last && last < fromDate)
            {
                return new SeriesResult<EndSeriesResponse>(SeriesOutcome.SeriesEnded);
            }

            if (fromDate <= series.StartsOn)
            {
                series.CancelledAt = nowUtc;
            }
            else
            {
                series.EndsOn = fromDate.AddDays(-1);
                series.OccurrenceCount = null;
            }

            var fromInstant = AvailabilityTime.DayStart(fromDate, zone);
            var cancelled = await db.Appointments
                .Where(a => a.SeriesId == series.Id && a.ProviderId == providerId && Active.Contains(a.Status)
                    && a.StartsAt >= fromInstant && a.StartsAt > nowUtc)
                .OrderBy(a => a.StartsAt).ThenBy(a => a.Id)
                .ToListAsync(c);
            foreach (var appointment in cancelled)
            {
                await Appointments.CancelByProviderAsync(db, appointment, EndCancellationReason, nowUtc, c, notifyPatient: false);
            }

            var hasEmail = await db.Patients.AsNoTracking()
                .AnyAsync(p => p.Id == series.PatientId && p.ProviderId == providerId && p.ContactEmail != null, c);
            var noticeOn = cancelled.FirstOrDefault()?.Id ?? await db.Appointments.AsNoTracking()
                .Where(a => a.SeriesId == series.Id)
                .OrderByDescending(a => a.StartsAt)
                .Select(a => (Guid?)a.Id)
                .FirstOrDefaultAsync(c);
            if (hasEmail && noticeOn is { } appointmentId)
            {
                AppointmentNotifications.QueueSeriesNotice(db, appointmentId, ReminderTemplates.SeriesEnded, nowUtc);
            }
            AvailabilityRules.AddAudit(db, providerId, EndedAction, AuditField,
                $"{series.Id} from {fromDate:yyyy-MM-dd} cancelled {cancelled.Count}", nowUtc);
            await db.SaveChangesAsync(c);
            return new SeriesResult<EndSeriesResponse>(SeriesOutcome.Ok, new EndSeriesResponse(cancelled.Count));
        }, ct);
    }

    // The date a series' "ended" notice announces: from its first date when it was cancelled whole, else the day after
    // its end date.
    public static DateOnly EndedFrom(AppointmentSeries series) =>
        series.CancelledAt is not null || series.EndsOn is null ? series.StartsOn : series.EndsOn.Value.AddDays(1);

    // What generation has actually produced so far, for the summary e-mail: the occurrences that exist in the series and
    // the dates generation covered whose occurrence does not exist (skipped because the slot was taken).
    public static async Task<(int Created, IReadOnlyList<DateOnly> Missing)> GenerationFactsAsync(
        AnamnysDbContext db, AppointmentSeries series, CancellationToken ct)
    {
        var indexes = (await db.Appointments.AsNoTracking()
                .Where(a => a.SeriesId == series.Id && a.SeriesIndex != null)
                .Select(a => a.SeriesIndex!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        var missing = new List<DateOnly>();
        if (series.GeneratedUntil is { } covered)
        {
            var last = SeriesPattern.LastDate(series);
            for (var k = 1; k <= SeriesPattern.MaxOccurrences; k++)
            {
                var date = SeriesPattern.DateOf(series, k);
                if (date > covered || (last is { } end && date > end) || k > (series.OccurrenceCount ?? int.MaxValue))
                {
                    break;
                }
                if (!indexes.Contains(k))
                {
                    missing.Add(date);
                }
            }
        }
        return (indexes.Count, missing);
    }

    internal static Task LockSeriesAsync(AnamnysDbContext db, Guid seriesId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AppointmentSeries\" WHERE \"Id\" = {seriesId} FOR UPDATE", ct);

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
}
