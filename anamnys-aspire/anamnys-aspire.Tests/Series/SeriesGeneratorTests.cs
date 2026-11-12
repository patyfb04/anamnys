using Anamnys.Server.Appointments.Series;
using Anamnys.Server.Availability;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static Anamnys.Tests.Series.SeriesScene;
using AvailabilityScene = Anamnys.Tests.Availability.AvailabilityScene;

namespace Anamnys.Tests.Series;

// Generation and the daily extension (design/specs/2026-11-09-appointment-series-design.md §3, §6).
[Collection(SharedAppHostCollection.Name)]
public class SeriesGeneratorTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeZoneInfo SaoPaulo = AvailabilityTime.Provider;

    private static List<DateOnly> Dates(IEnumerable<Appointment> occurrences) =>
        [.. occurrences.Select(a => AvailabilityTime.LocalDate(a.StartsAt, SaoPaulo))];

    [Fact]
    public async Task Weekly_WithoutEnd_GeneratesTwelveWeeksAhead_AtTheSameLocalTime()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);

        // Act
        var created = await CreateSeriesAsync(s, Ct);

        // Assert
        var occurrences = await OccurrencesAsync(s, Ct, created.SeriesId);
        created.Generated.Should().Be(11);
        occurrences.Should().HaveCount(11);
        Dates(occurrences).Should().Equal(Enumerable.Range(0, 11).Select(i => new DateOnly(2026, 11, 17).AddDays(7 * i)));
        occurrences.Select(a => a.SeriesIndex).Should().Equal(Enumerable.Range(1, 11).Select(i => (int?)i));
        occurrences.Should().OnlyContain(a => TimeZoneInfo.ConvertTime(a.StartsAt, SaoPaulo).TimeOfDay == new TimeSpan(14, 0, 0)
            && a.EndsAt - a.StartsAt == TimeSpan.FromMinutes(50) && a.Status == "scheduled" && !a.Detached
            && a.CreatedBy == "provider" && a.Modality == "online" && a.Timezone == "America/Sao_Paulo");
        occurrences[0].Id.Should().Be(created.FirstAppointmentId);
        var series = await SeriesAsync(s, created.SeriesId, Ct);
        series.GeneratedUntil.Should().Be(Today.AddDays(84));
        series.Weekday.Should().Be(2);
        series.StartTime.Should().Be(new TimeOnly(14, 0));
        series.StartsOn.Should().Be(new DateOnly(2026, 11, 17));
        series.RecurrenceRule.Should().Be("WEEKLY;INTERVAL=1;BYDAY=TU");
    }

    [Fact]
    public async Task EveryTwoWeeks_GeneratesEveryOtherWeek()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);

        // Act
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, interval: 2));

        // Assert
        Dates(await OccurrencesAsync(s, Ct, created.SeriesId)).Should().Equal(
            new DateOnly(2026, 11, 17), new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15), new DateOnly(2026, 12, 29),
            new DateOnly(2027, 1, 12), new DateOnly(2027, 1, 26));
    }

    [Fact]
    public async Task EndByDate_AndEndByCount_StopThere()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);

        // Act
        var byDate = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, endsOn: new DateOnly(2026, 12, 8)));
        var byCount = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, startsAt: Tuesday14.AddHours(2), count: 3));

        // Assert
        Dates(await OccurrencesAsync(s, Ct, byDate.SeriesId)).Should().Equal(
            new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 24), new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 8));
        (await OccurrencesAsync(s, Ct, byCount.SeriesId)).Select(a => a.SeriesIndex).Should().Equal(1, 2, 3);
        (await SeriesAsync(s, byDate.SeriesId, Ct)).GeneratedUntil.Should().Be(new DateOnly(2026, 12, 8));
    }

    [Fact]
    public async Task DailyExtension_AddsTheNextWeek_AndIsIdempotent_WithoutSummaryEmails()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct);
        var ended = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, startsAt: Tuesday14.AddHours(2), count: 2));
        var weekLater = Now.AddDays(7);

        // Act
        await using var db = s.CreateDbContext();
        var first = await SeriesExtensionJob.RunOnceAsync(db, weekLater, Ct, providerId: s.Provider);
        var second = await SeriesExtensionJob.RunOnceAsync(db, weekLater, Ct, providerId: s.Provider);

        // Assert
        first.Should().Be((1, 1, 0, 0));
        second.Should().Be((0, 0, 0, 0));
        var occurrences = await OccurrencesAsync(s, Ct, created.SeriesId);
        occurrences.Should().HaveCount(12);
        occurrences[^1].SeriesIndex.Should().Be(12);
        Dates(occurrences)[^1].Should().Be(new DateOnly(2027, 2, 2));
        (await OccurrencesAsync(s, Ct, ended.SeriesId)).Should().HaveCount(2);
        var reminders = await RemindersAsync(s, Ct);
        reminders.Count(r => r.TemplateKey == "series_scheduled").Should().Be(2);
        reminders.Where(r => r.AppointmentId == occurrences[^1].Id).Select(r => r.TemplateKey)
            .Should().BeEquivalentTo("confirmation", "reminder_1h");
    }

    [Fact]
    public async Task Generation_KeepsTheLocalTimeAcrossADaylightSavingChange()
    {
        // Arrange: a zone with DST (New York leaves it on 2026-11-01); 10:00 local is 14:00 UTC before and 15:00 UTC after.
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        var series = new AppointmentSeries
        {
            Id = Guid.NewGuid(), PatientId = s.Patient, ProviderId = s.Provider, RecurrenceRule = "WEEKLY;INTERVAL=1;BYDAY=TU",
            StartsOn = new DateOnly(2026, 10, 20), OccurrenceCount = 4, DefaultMinutes = 50, DefaultModality = "presencial",
            Interval = 1, Weekday = 2, StartTime = new TimeOnly(10, 0), Timezone = "America/New_York", CreatedAt = Now,
        };
        db.AppointmentSeries.Add(series);

        // Act
        var created = (await SeriesGenerator.ExtendAsync(db, series, new DateOnly(2026, 10, 15), Ct, new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero))).Created;
        await db.SaveChangesAsync(Ct);

        // Assert
        created.Select(a => a.StartsAt.UtcDateTime.Hour).Should().Equal(14, 14, 15, 15);
        created.Should().OnlyContain(a => a.Timezone == "America/New_York" && a.Modality == "presencial");
    }

    [Fact]
    public async Task Generation_CreatesOccurrencesOnBlocksAndHolidays_AndSkipsATakenSlot()
    {
        // Arrange: a block over the second Tuesday, Christmas on the second Friday, and an appointment in the third Tuesday's slot.
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        await s.AddBlockAsync(s.Provider, Tuesday14.AddDays(7).AddHours(-1), Tuesday14.AddDays(7).AddHours(3), Ct);
        var taken = await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Tuesday14.AddDays(14), "scheduled", Ct);
        var friday = new DateTimeOffset(2026, 12, 18, 9, 0, 0, TimeSpan.FromHours(-3));

        // Act
        var tuesdays = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 4));
        var fridays = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, startsAt: friday, count: 2));

        // Assert
        var occurrences = await OccurrencesAsync(s, Ct, tuesdays.SeriesId);
        occurrences.Select(a => a.SeriesIndex).Should().Equal(1, 2, 4);
        tuesdays.Generated.Should().Be(3);
        tuesdays.Skipped.Should().Equal(new SkippedOccurrence(3, Tuesday14.AddDays(14)));
        fridays.Skipped.Should().BeEmpty();
        Dates(await OccurrencesAsync(s, Ct, fridays.SeriesId)).Should().Equal(new DateOnly(2026, 12, 18), new DateOnly(2026, 12, 25));
        (await s.Db.CountAsync("""SELECT count(*) FROM "Appointments" WHERE "Id" = @id AND "Status" = 'scheduled'""", Ct, ("id", taken)))
            .Should().Be(1);
        (await s.Db.CountAsync("""SELECT count(*) FROM "Appointments" WHERE "ProviderId" = @p AND "Status" = 'cancelled'""", Ct, ("p", s.Provider)))
            .Should().Be(0);
    }

    [Fact]
    public async Task Extension_SkipsCancelledSeriesAndArchivedPatients()
    {
        // Arrange: one series of an archived patient, one cancelled series of another patient.
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct);
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @p""", Ct, ("p", s.Patient));
        var clara = await s.Db.AddPatientAsync(s.Provider, "Clara", Ct);
        var cancelled = await CreateSeriesAsync(s, Ct, Weekly(clara, startsAt: Tuesday14.AddHours(2)));
        await s.Db.ExecuteAsync("""UPDATE "AppointmentSeries" SET "CancelledAt" = now() WHERE "Id" = @id""", Ct, ("id", cancelled.SeriesId));

        // Act
        await using var db = s.CreateDbContext();
        var run = await SeriesExtensionJob.RunOnceAsync(db, Now.AddDays(14), Ct, providerId: s.Provider);

        // Assert
        run.Should().Be((0, 0, 0, 0));
        (await OccurrencesAsync(s, Ct, created.SeriesId)).Should().HaveCount(11);
        (await OccurrencesAsync(s, Ct, cancelled.SeriesId)).Should().HaveCount(11);
    }

    [Fact]
    public async Task Extension_ResumedAfterALongPause_CreatesOnlyFutureOccurrences_AndReportsNoPastDates()
    {
        // Arrange: a never-ending series covered until 2027-02-01; the job next runs 100 days later (an archived patient
        // brought back, or a stopped worker), on Wednesday 2027-05-12 at 12:00.
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct);
        var resumedAt = Now.AddDays(184);
        var resumedOn = AvailabilityTime.LocalDate(resumedAt, SaoPaulo);
        (await SeriesAsync(s, created.SeriesId, Ct)).GeneratedUntil.Should().Be(resumedOn.AddDays(-100));
        await using var db = s.CreateDbContext();

        // Act
        var run = await SeriesExtensionJob.RunOnceAsync(db, resumedAt, Ct, providerId: s.Provider);

        // Assert: the Tuesdays from 2027-02-02 to 2027-05-11 are passed over; 12 Tuesdays from 2027-05-18 are created.
        run.Should().Be((1, 12, 0, 0));
        var occurrences = await OccurrencesAsync(s, Ct, created.SeriesId);
        var resumed = occurrences.Where(a => a.CreatedAt > Now).ToList();
        occurrences.Should().HaveCount(11 + 12);
        resumed.Should().HaveCount(12).And.OnlyContain(a => a.StartsAt > resumedAt);
        Dates(resumed)[0].Should().Be(new DateOnly(2027, 5, 18));
        Dates(resumed)[^1].Should().Be(new DateOnly(2027, 8, 3));
        (await SeriesAsync(s, created.SeriesId, Ct)).GeneratedUntil.Should().Be(resumedOn.AddDays(84));
        (await db.Notifications.AsNoTracking().CountAsync(n => n.ProviderId == s.Provider, Ct)).Should().Be(0);
        (await RemindersAsync(s, Ct)).Where(r => r.TemplateKey == "confirmation").Should().HaveCount(11 + 12)
            .And.OnlyContain(r => r.ScheduledFor >= Now);
    }

    [Fact]
    public async Task Extension_ReportsATakenSlot_AsOneBellNotice_OncePerOccurrence()
    {
        // Arrange: the 12th Tuesday (2027-02-02 14:00) is taken before the extension reaches it.
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct);
        var twelfth = Tuesday14.AddDays(77);
        await s.Db.AddAppointmentAsync(s.Provider, s.Patient, twelfth, "scheduled", Ct);
        await using var db = s.CreateDbContext();

        // Act: run, then forget the coverage and run again (the notice must not repeat).
        var first = await SeriesExtensionJob.RunOnceAsync(db, Now.AddDays(7), Ct, providerId: s.Provider);
        await s.Db.ExecuteAsync("""UPDATE "AppointmentSeries" SET "GeneratedUntil" = '2027-02-01' WHERE "Id" = @id""", Ct, ("id", created.SeriesId));
        var second = await SeriesExtensionJob.RunOnceAsync(db, Now.AddDays(7), Ct, providerId: s.Provider);
        var inbox = await ProviderNotifications.ListAsync(db, s.Provider, 1, Ct);

        // Assert
        first.Should().Be((1, 0, 1, 0));
        second.Should().Be((1, 0, 1, 0));
        var notices = await db.Notifications.AsNoTracking().Where(n => n.ProviderId == s.Provider).ToListAsync(Ct);
        notices.Should().ContainSingle().Which.Should().Match<Notification>(n => n.Kind == "series_occurrence_skipped"
            && n.SubjectType == "appointment_series" && n.SubjectId == created.SeriesId && n.Channel == "in_app"
            && n.DedupeKey == $"series_skipped:{created.SeriesId}:12" && n.ReadAt == null);
        var item = inbox.Items.Should().ContainSingle().Subject;
        item.Kind.Should().Be("series_occurrence_skipped");
        item.SeriesId.Should().Be(created.SeriesId);
        item.PatientId.Should().Be(s.Patient);
        item.PatientName.Should().Be("Benedita Silva");
        item.StartsAt.Should().Be(twelfth);
        item.AppointmentId.Should().BeNull();
        (await OccurrencesAsync(s, Ct, created.SeriesId)).Should().HaveCount(11);
    }
}
