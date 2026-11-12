using Anamnys.Server.Appointments;
using Anamnys.Server.Appointments.Series;
using Anamnys.Server.Data.Entities;
using Anamnys.Tests.Availability;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Series;

// A scratch database from the bootstrap script (which carries design/migrations/2026-11-10-appointment-series.sql), so
// these tests do not depend on the dev database having the migration applied. Now is Monday 2026-11-09, 12:00 in São
// Paulo; the default series starts on Tuesday 2026-11-17 at 14:00 (17:00 UTC).
internal static class SeriesScene
{
    public static readonly DateTimeOffset Now = AvailabilityScene.Now;
    public static readonly DateOnly Today = AvailabilityScene.Today;
    public static readonly DateTimeOffset Tuesday14 = new(2026, 11, 17, 14, 0, 0, TimeSpan.FromHours(-3));

    public static CreateSeriesRequest Weekly(
        Guid patient, DateTimeOffset? startsAt = null, int interval = 1, DateOnly? endsOn = null, int? count = null, bool acknowledge = false) =>
        new(patient, startsAt ?? Tuesday14, 50, "online", interval, endsOn, count, acknowledge);

    public static async Task<SeriesCreatedResponse> CreateSeriesAsync(
        AvailabilityScene s, CancellationToken ct, CreateSeriesRequest? request = null, DateTimeOffset? now = null)
    {
        await using var db = s.CreateDbContext();
        var result = await SeriesService.CreateAsync(db, s.Provider, request ?? Weekly(s.Patient), now ?? Now, ct);
        if (result.Outcome != SeriesOutcome.Ok)
        {
            throw new InvalidOperationException(result.Outcome.ToString());
        }
        return result.Value!;
    }

    public static async Task<List<Appointment>> OccurrencesAsync(AvailabilityScene s, CancellationToken ct, Guid? seriesId = null)
    {
        await using var db = s.CreateDbContext();
        return await db.Appointments.AsNoTracking()
            .Where(a => a.ProviderId == s.Provider && (seriesId == null || a.SeriesId == seriesId))
            .OrderBy(a => a.StartsAt).ThenBy(a => a.Id)
            .ToListAsync(ct);
    }

    public static async Task<AppointmentSeries> SeriesAsync(AvailabilityScene s, Guid id, CancellationToken ct)
    {
        await using var db = s.CreateDbContext();
        return await db.AppointmentSeries.AsNoTracking().SingleAsync(x => x.Id == id, ct);
    }

    public static async Task<List<Reminder>> RemindersAsync(AvailabilityScene s, CancellationToken ct)
    {
        await using var db = s.CreateDbContext();
        return await (
                from r in db.Reminders.AsNoTracking()
                join a in db.Appointments.AsNoTracking() on r.AppointmentId equals a.Id
                where a.ProviderId == s.Provider
                orderby r.ScheduledFor, r.TemplateKey
                select r)
            .ToListAsync(ct);
    }

    public static async Task SetStatusAsync(AvailabilityScene s, Guid appointmentId, string status, CancellationToken ct)
    {
        await using var db = s.CreateDbContext();
        var outcome = await global::Anamnys.Server.Appointments.Appointments.SetStatusAsync(
            db, s.Provider, appointmentId, new ChangeAppointmentStatusRequest(status, null), Now, ct);
        if (outcome != AppointmentOutcome.Ok)
        {
            throw new InvalidOperationException(outcome.ToString());
        }
    }

    public static async Task RescheduleAsync(AvailabilityScene s, Guid appointmentId, DateTimeOffset startsAt, CancellationToken ct)
    {
        await using var db = s.CreateDbContext();
        var result = await global::Anamnys.Server.Appointments.Appointments.UpdateWithReasonsAsync(
            db, s.Provider, appointmentId, new UpdateAppointmentRequest(startsAt, 50, "online", true), Now, ct);
        if (result.Outcome != AppointmentOutcome.Ok)
        {
            throw new InvalidOperationException(result.Outcome.ToString());
        }
    }
}
