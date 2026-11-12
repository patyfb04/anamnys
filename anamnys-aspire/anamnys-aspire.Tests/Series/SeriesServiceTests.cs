using Anamnys.Server.Appointments;
using Anamnys.Server.Appointments.Series;
using Anamnys.Server.Availability;
using Anamnys.Server.Notifications;
using Anamnys.Tests.Notifications;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static Anamnys.Tests.Series.SeriesScene;
using AvailabilityScene = Anamnys.Tests.Availability.AvailabilityScene;

namespace Anamnys.Tests.Series;

using Appointments = Anamnys.Server.Appointments.Appointments;

// Create, notices, single occurrences, split and end (design/specs/2026-11-09-appointment-series-design.md §3, §6).
[Collection(SharedAppHostCollection.Name)]
public class SeriesServiceTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Uri PortalBase = new("http://localhost:5274/patient/");
    private static readonly TimeZoneInfo SaoPaulo = AvailabilityTime.Provider;

    // ---- Create and notices ----

    [Fact]
    public async Task Create_QueuesOneSummary_AndEachConfirmation48HoursAhead_OrNowWhenCloser()
    {
        // Arrange: the first session is tomorrow at 14:00 (26 h away), the others a week apart.
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var tomorrow = new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.FromHours(-3));

        // Act
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, startsAt: tomorrow, count: 3));

        // Assert
        var occurrences = await OccurrencesAsync(s, Ct, created.SeriesId);
        var reminders = await RemindersAsync(s, Ct);
        reminders.Where(r => r.TemplateKey == "series_scheduled").Should().ContainSingle()
            .Which.Should().Match<Server.Data.Entities.Reminder>(r => r.AppointmentId == created.FirstAppointmentId && r.ScheduledFor == Now
                && r.DeliveryStatus == "pending" && r.ConfirmationId == null);
        var confirmations = reminders.Where(r => r.TemplateKey == "confirmation").ToDictionary(r => r.AppointmentId, r => r.ScheduledFor);
        confirmations[occurrences[0].Id].Should().Be(Now);
        confirmations[occurrences[1].Id].Should().Be(occurrences[1].StartsAt.AddHours(-48));
        confirmations[occurrences[2].Id].Should().Be(occurrences[2].StartsAt.AddHours(-48));
        reminders.Where(r => r.TemplateKey == "reminder_1h").Select(r => r.ScheduledFor)
            .Should().Equal(occurrences.Select(a => a.StartsAt.AddHours(-1)));
        reminders.Should().NotContain(r => r.TemplateKey == "cancellation");
        await using var db = s.CreateDbContext();
        (await db.AppointmentConfirmations.CountAsync(c => occurrences.Select(a => a.Id).Contains(c.AppointmentId) && c.DeadlineAt == null, Ct))
            .Should().Be(3);
        (await s.AuditAsync(Ct)).Should().ContainSingle(a => a.StartsWith("series_created series " + created.SeriesId)
            && a.EndsWith("generated 3 skipped 0"));
    }

    [Fact]
    public async Task Worker_SendsTheSummary_ThenEachConfirmationWithItsDeadlineFromTheSendTime()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 10));
        var sender = new RecordingEmailSender();
        await using var db = s.CreateDbContext();
        var sendAt = Tuesday14.AddHours(-48);

        // Act
        var now = await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: s.Provider);
        var later = await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, sendAt, Ct, providerId: s.Provider);

        // Assert
        now.Should().Be(1);
        later.Should().Be(1);
        sender.Sent[0].Subject.Should().Be("Sessões marcadas com Dra. Teste");
        sender.Sent[0].Text.Should().Contain("toda terça às 14:00, a partir de 17/11, 10 sessões (Online)");
        sender.Sent[0].Text.Should().NotContain("Silva");
        sender.Sent[1].Subject.Should().Be("Confirme sua consulta com Dra. Teste");
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == created.FirstAppointmentId, Ct);
        confirmation.DeadlineAt.Should().Be(sendAt.ToUniversalTime().AddHours(ConfirmationRules.DefaultHours));
    }

    [Fact]
    public async Task Create_RefusesBadIntervalAndEnd_AForeignPatient_ATakenFirstSlot_AndAPastStart_WritingNothing()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Tuesday14, "scheduled", Ct);
        await using var db = s.CreateDbContext();
        var later = Tuesday14.AddHours(3);

        // Act
        var interval = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Patient, startsAt: later, interval: 3), Now, Ct);
        var both = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Patient, startsAt: later, endsOn: Today.AddDays(30), count: 3), Now, Ct);
        var backwards = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Patient, startsAt: later, endsOn: new DateOnly(2026, 11, 16)), Now, Ct);
        var zero = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Patient, startsAt: later, count: 0), Now, Ct);
        var tooMany = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Patient, startsAt: later, count: 521), Now, Ct);
        var foreign = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Stranger, startsAt: later), Now, Ct);
        var taken = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Patient), Now, Ct);
        var lastYear = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Patient, startsAt: later.AddYears(-1)), Now, Ct);
        var rightNow = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Patient, startsAt: Now), Now, Ct);

        // Assert
        lastYear.Outcome.Should().Be(SeriesOutcome.StartInPast);
        rightNow.Outcome.Should().Be(SeriesOutcome.StartInPast);
        interval.Outcome.Should().Be(SeriesOutcome.InvalidInterval);
        both.Outcome.Should().Be(SeriesOutcome.InvalidEnd);
        backwards.Outcome.Should().Be(SeriesOutcome.InvalidEnd);
        zero.Outcome.Should().Be(SeriesOutcome.InvalidEnd);
        tooMany.Outcome.Should().Be(SeriesOutcome.InvalidEnd);
        foreign.Outcome.Should().Be(SeriesOutcome.NotFound);
        taken.Outcome.Should().Be(SeriesOutcome.Overlap);
        (await s.Db.CountAsync("""SELECT count(*) FROM "AppointmentSeries" """, Ct)).Should().Be(0);
        (await OccurrencesAsync(s, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Create_OutsideAvailability_ChecksTheFirstSessionOnly_AndIsAuditedWhenAcknowledged()
    {
        // Arrange: weekday hours; the first session at 19:00 is outside them.
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(Today, Ct);
        var evening = Tuesday14.AddHours(5);
        await using var db = s.CreateDbContext();

        // Act
        var refused = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Patient, startsAt: evening, count: 3), Now, Ct);
        var acknowledged = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Patient, startsAt: evening, count: 3, acknowledge: true), Now, Ct);
        var inside = await SeriesService.CreateAsync(db, s.Provider, Weekly(s.Patient, count: 3), Now, Ct);

        // Assert
        refused.Outcome.Should().Be(SeriesOutcome.OutsideAvailability);
        refused.Reasons.Should().ContainSingle(r => r.Kind == OutsideAvailabilityReason.OutsideHours);
        acknowledged.Outcome.Should().Be(SeriesOutcome.Ok);
        inside.Outcome.Should().Be(SeriesOutcome.Ok);
        var audit = await s.AuditAsync(Ct);
        audit.Should().ContainSingle(a => a.StartsWith("appointment_outside_availability appointment " + acknowledged.Value!.FirstAppointmentId));
        audit.Count(a => a.StartsWith("series_created")).Should().Be(2);
    }

    // ---- Single occurrences ----

    [Fact]
    public async Task RescheduleOne_SetsDetached_AndCancelOne_LeavesTheOthers()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 4));
        var occurrences = await OccurrencesAsync(s, Ct, created.SeriesId);

        // Act
        await RescheduleAsync(s, occurrences[1].Id, occurrences[1].StartsAt.AddDays(1), Ct);
        await SetStatusAsync(s, occurrences[2].Id, "cancelled", Ct);

        // Assert
        var after = (await OccurrencesAsync(s, Ct, created.SeriesId)).ToDictionary(a => a.SeriesIndex!.Value);
        after[2].Detached.Should().BeTrue();
        after[2].StartsAt.Should().Be(occurrences[1].StartsAt.AddDays(1));
        after[3].Status.Should().Be("cancelled");
        after[3].Detached.Should().BeFalse();
        new[] { after[1], after[4] }.Should().OnlyContain(a => a.Status == "scheduled" && !a.Detached);
        (await SeriesAsync(s, created.SeriesId, Ct)).CancelledAt.Should().BeNull();
        var reminders = await RemindersAsync(s, Ct);
        reminders.Should().ContainSingle(r => r.TemplateKey == "cancellation" && r.AppointmentId == occurrences[2].Id);
        reminders.Should().ContainSingle(r => r.TemplateKey == "series_scheduled" && r.DeliveryStatus == "pending");
    }

    [Fact]
    public async Task List_CarriesTheSeriesFields()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 2));
        var single = await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Tuesday14.AddHours(-3), "scheduled", Ct);
        await using var db = s.CreateDbContext();

        // Act
        var list = await Appointments.ListAsync(db, s.Provider, Tuesday14.AddDays(-1), Tuesday14.AddDays(14), [], Ct);

        // Assert
        list.Items.Should().HaveCount(3);
        var one = list.Items.Single(i => i.Id == single);
        one.SeriesId.Should().BeNull();
        one.SeriesDescription.Should().BeNull();
        var first = list.Items.Single(i => i.Id == created.FirstAppointmentId);
        first.SeriesId.Should().Be(created.SeriesId);
        first.SeriesIndex.Should().Be(1);
        first.Detached.Should().BeFalse();
        first.SeriesDescription.Should().Be("toda terça às 14:00");
    }

    // ---- Split ----

    [Fact]
    public async Task Split_MovesOnlyUntouchedScheduledOccurrences_KeepsConfirmedAndDetached_AndCarriesTheCount()
    {
        // Arrange: ten Tuesdays; the 4th confirmed, the 5th rescheduled on its own; split from the 3rd to Thursdays 16:00.
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(Today, Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 10));
        var old = await OccurrencesAsync(s, Ct, created.SeriesId);
        await SetStatusAsync(s, old[3].Id, "confirmed", Ct);
        var movedFifth = old[4].StartsAt.AddHours(1);
        await RescheduleAsync(s, old[4].Id, movedFifth, Ct);
        var body = new SplitSeriesRequest(old[2].Id, 4, "16:00", 45, "presencial");
        await using var db = s.CreateDbContext();

        // Act
        var preview = await SeriesService.PreviewSplitAsync(db, s.Provider, created.SeriesId, body, Now, Ct);
        var split = await SeriesService.SplitAsync(db, s.Provider, created.SeriesId, body, Now, Ct);

        // Assert
        var thursday = new DateTimeOffset(2026, 12, 3, 16, 0, 0, TimeSpan.FromHours(-3));
        preview.Outcome.Should().Be(SeriesOutcome.Ok);
        preview.Value!.NewFirstStartsAt.Should().Be(thursday);
        preview.Value.Moving.Should().Be(6);
        preview.Value.Staying.Select(x => (x.Id, x.Reason)).Should().Equal((old[3].Id, "confirmed"), (old[4].Id, "detached"));
        split.Outcome.Should().Be(SeriesOutcome.Ok);
        var newId = split.Value!.NewSeriesId;

        var oldSeries = await SeriesAsync(s, created.SeriesId, Ct);
        oldSeries.EndsOn.Should().Be(new DateOnly(2026, 11, 30));
        oldSeries.OccurrenceCount.Should().BeNull();
        oldSeries.CancelledAt.Should().BeNull();
        var newSeries = await SeriesAsync(s, newId, Ct);
        newSeries.PreviousSeriesId.Should().Be(created.SeriesId);
        newSeries.OccurrenceCount.Should().Be(8);
        newSeries.StartsOn.Should().Be(new DateOnly(2026, 12, 3));
        newSeries.Weekday.Should().Be(4);
        newSeries.StartTime.Should().Be(new TimeOnly(16, 0));
        newSeries.DefaultMinutes.Should().Be(45);
        newSeries.DefaultModality.Should().Be("presencial");
        newSeries.RecurrenceRule.Should().Be("WEEKLY;INTERVAL=1;BYDAY=TH");

        (await OccurrencesAsync(s, Ct, created.SeriesId)).Select(a => a.Id).Should().Equal(old[0].Id, old[1].Id);
        var next = await OccurrencesAsync(s, Ct, newId);
        next.Select(a => a.SeriesIndex).Should().BeEquivalentTo(new int?[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var confirmed = next.Single(a => a.Id == old[3].Id);
        confirmed.Should().Match<Server.Data.Entities.Appointment>(a => a.SeriesIndex == 2 && a.Detached && a.Status == "confirmed" && a.StartsAt == old[3].StartsAt);
        var detached = next.Single(a => a.Id == old[4].Id);
        detached.Should().Match<Server.Data.Entities.Appointment>(a => a.SeriesIndex == 3 && a.Detached && a.StartsAt == movedFifth);
        var regenerated = next.Where(a => a.Id != old[3].Id && a.Id != old[4].Id).ToList();
        regenerated.Should().HaveCount(6);
        regenerated.Should().OnlyContain(a => !a.Detached && a.Modality == "presencial" && a.EndsAt - a.StartsAt == TimeSpan.FromMinutes(45)
            && TimeZoneInfo.ConvertTime(a.StartsAt, SaoPaulo).DayOfWeek == DayOfWeek.Thursday
            && TimeZoneInfo.ConvertTime(a.StartsAt, SaoPaulo).TimeOfDay == new TimeSpan(16, 0, 0));
        regenerated.Single(a => a.SeriesIndex == 1).StartsAt.Should().Be(thursday);
        (await OccurrencesAsync(s, Ct)).Where(a => a.Status != "cancelled").Should().HaveCount(10);

        var reminders = await RemindersAsync(s, Ct);
        reminders.Count(r => r.TemplateKey == "series_scheduled").Should().Be(2);
        reminders.Should().ContainSingle(r => r.TemplateKey == "series_scheduled" && r.AppointmentId == regenerated.Single(a => a.SeriesIndex == 1).Id);
        reminders.Should().NotContain(r => r.TemplateKey == "cancellation");
        (await s.AuditAsync(Ct)).Should().ContainSingle(a => a.StartsWith("series_split series " + created.SeriesId + " -> " + newId)
            && a.EndsWith("from 3 moved 6 kept 2 relinked 2 skipped 0"));
    }

    [Fact]
    public async Task Split_ChecksAvailabilityOnTheNewFirstDate_AndRefusesPastOrForeignOccurrences()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(Today, Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 4));
        var occurrences = await OccurrencesAsync(s, Ct, created.SeriesId);
        var other = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, startsAt: Tuesday14.AddHours(2), count: 1));
        var evening = new SplitSeriesRequest(occurrences[1].Id, 3, "20:00", 50, "online");
        await using var db = s.CreateDbContext();

        // Act
        var refused = await SeriesService.SplitAsync(db, s.Provider, created.SeriesId, evening, Now, Ct);
        var unchanged = await OccurrencesAsync(s, Ct, created.SeriesId);
        var wrongSeries = await SeriesService.SplitAsync(db, s.Provider, other.SeriesId, evening, Now, Ct);
        var foreign = await SeriesService.PreviewSplitAsync(db, s.OtherProvider, created.SeriesId, evening, Now, Ct);
        var late = await SeriesService.PreviewSplitAsync(db, s.Provider, created.SeriesId, evening, occurrences[1].StartsAt.AddMinutes(1), Ct);
        var acknowledged = await SeriesService.SplitAsync(db, s.Provider, created.SeriesId, evening with { AcknowledgeOutsideAvailability = true }, Now, Ct);

        // Assert
        refused.Outcome.Should().Be(SeriesOutcome.OutsideAvailability);
        refused.Reasons.Should().ContainSingle(r => r.Kind == OutsideAvailabilityReason.OutsideHours);
        unchanged.Select(a => a.Id).Should().Equal(occurrences.Select(a => a.Id));
        wrongSeries.Outcome.Should().Be(SeriesOutcome.NotFound);
        foreign.Outcome.Should().Be(SeriesOutcome.NotFound);
        late.Outcome.Should().Be(SeriesOutcome.InvalidOccurrence);
        acknowledged.Outcome.Should().Be(SeriesOutcome.Ok);
        var first = (await OccurrencesAsync(s, Ct, acknowledged.Value!.NewSeriesId)).First();
        TimeZoneInfo.ConvertTime(first.StartsAt, SaoPaulo).DateTime.Should().Be(new DateTime(2026, 11, 25, 20, 0, 0));
        (await s.AuditAsync(Ct)).Should().Contain(a => a.StartsWith("appointment_outside_availability appointment " + first.Id));
    }

    [Fact]
    public async Task Split_FromTheFirstOccurrence_CancelsTheOldSeriesHeader()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, endsOn: new DateOnly(2026, 12, 15)));
        await using var db = s.CreateDbContext();

        // Act
        var split = await SeriesService.SplitAsync(db, s.Provider, created.SeriesId,
            new SplitSeriesRequest(created.FirstAppointmentId, 1, "09:00", 50, "online"), Now, Ct);

        // Assert
        split.Outcome.Should().Be(SeriesOutcome.Ok);
        (await SeriesAsync(s, created.SeriesId, Ct)).CancelledAt.Should().Be(Now);
        (await OccurrencesAsync(s, Ct, created.SeriesId)).Should().BeEmpty();
        var newSeries = await SeriesAsync(s, split.Value!.NewSeriesId, Ct);
        newSeries.EndsOn.Should().Be(new DateOnly(2026, 12, 15));
        newSeries.StartsOn.Should().Be(new DateOnly(2026, 11, 23));
        (await OccurrencesAsync(s, Ct, newSeries.Id)).Should().HaveCount(4);
    }

    [Fact]
    public async Task Split_WhenTheChosenOccurrenceStays_PreviewsAndChecksTheFirstSessionActuallyGenerated()
    {
        // Arrange: six Tuesdays; the 3rd (2026-12-01) is confirmed, so it keeps its time and its Thursday (12-03) never
        // exists in the new series. A block covers Thursday 12-10 at 16:00, the first session really generated.
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(Today, Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 6));
        var old = await OccurrencesAsync(s, Ct, created.SeriesId);
        await SetStatusAsync(s, old[2].Id, "confirmed", Ct);
        var secondThursday = new DateTimeOffset(2026, 12, 10, 16, 0, 0, TimeSpan.FromHours(-3));
        await s.AddBlockAsync(s.Provider, secondThursday.AddHours(-1), secondThursday.AddHours(2), Ct);
        var body = new SplitSeriesRequest(old[2].Id, 4, "16:00", 50, "online");
        await using var db = s.CreateDbContext();

        // Act
        var preview = await SeriesService.PreviewSplitAsync(db, s.Provider, created.SeriesId, body, Now, Ct);
        var refused = await SeriesService.SplitAsync(db, s.Provider, created.SeriesId, body, Now, Ct);
        var acknowledged = await SeriesService.SplitAsync(db, s.Provider, created.SeriesId, body with { AcknowledgeOutsideAvailability = true }, Now, Ct);

        // Assert
        preview.Outcome.Should().Be(SeriesOutcome.Ok);
        preview.Value!.NewFirstStartsAt.Should().Be(secondThursday);
        preview.Value.Moving.Should().Be(3);
        preview.Value.Staying.Should().ContainSingle(x => x.Id == old[2].Id && x.Reason == "confirmed");
        refused.Outcome.Should().Be(SeriesOutcome.OutsideAvailability);
        refused.Reasons.Should().ContainSingle(r => r.Kind == OutsideAvailabilityReason.Block);
        acknowledged.Outcome.Should().Be(SeriesOutcome.Ok);
        var next = await OccurrencesAsync(s, Ct, acknowledged.Value!.NewSeriesId);
        next.Select(a => a.SeriesIndex).Should().Equal(1, 2, 3, 4);
        var generatedFirst = next.Single(a => a.SeriesIndex == 2);
        generatedFirst.StartsAt.Should().Be(secondThursday);
        (await s.AuditAsync(Ct)).Should().ContainSingle(a => a.StartsWith("appointment_outside_availability appointment " + generatedFirst.Id));
    }

    // ---- End ----

    [Fact]
    public async Task End_CancelsFromTheDate_LeavesEarlierOnes_AndSendsOneNoticeOnly()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct);
        var from = new DateOnly(2026, 12, 8);
        await using var db = s.CreateDbContext();

        // Act
        var ended = await SeriesService.EndAsync(db, s.Provider, created.SeriesId, new EndSeriesRequest(from), Now, Ct);
        var again = await SeriesService.EndAsync(db, s.Provider, created.SeriesId, new EndSeriesRequest(from.AddDays(7)), Now, Ct);
        var sender = new RecordingEmailSender();
        await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: s.Provider);
        var extension = await SeriesExtensionJob.RunOnceAsync(db, Now.AddDays(7), Ct, providerId: s.Provider);

        // Assert
        ended.Outcome.Should().Be(SeriesOutcome.Ok);
        ended.Value!.Cancelled.Should().Be(8);
        again.Outcome.Should().Be(SeriesOutcome.SeriesEnded);
        var series = await SeriesAsync(s, created.SeriesId, Ct);
        series.EndsOn.Should().Be(new DateOnly(2026, 12, 7));
        series.CancelledAt.Should().BeNull();
        var occurrences = await OccurrencesAsync(s, Ct, created.SeriesId);
        occurrences.Take(3).Should().OnlyContain(a => a.Status == "scheduled");
        occurrences.Skip(3).Should().OnlyContain(a => a.Status == "cancelled" && a.CancelledBy == "provider");
        var reminders = await RemindersAsync(s, Ct);
        reminders.Should().NotContain(r => r.TemplateKey == "cancellation");
        reminders.Should().ContainSingle(r => r.TemplateKey == "series_ended" && r.AppointmentId == occurrences[3].Id);
        reminders.Where(r => occurrences.Skip(3).Select(a => a.Id).Contains(r.AppointmentId) && r.TemplateKey != "series_ended")
            .Should().OnlyContain(r => r.DeliveryStatus == "cancelled");
        sender.Sent.Select(m => m.Subject).Should().BeEquivalentTo("Sessões marcadas com Dra. Teste", "Sessões encerradas");
        sender.Sent.Single(m => m.Subject == "Sessões encerradas").Text.Should().Contain("(toda terça às 14:00) foram encerradas a partir de 08/12/2026");
        extension.Should().Be((0, 0, 0, 0));
        (await s.AuditAsync(Ct)).Should().ContainSingle(a => a == $"series_ended series {created.SeriesId} from 2026-12-08 cancelled 8");
    }

    [Fact]
    public async Task End_FromTheFirstDate_CancelsTheSeries_AndRefusesPastDatesAndForeignSeries()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 3));
        await using var db = s.CreateDbContext();

        // Act
        var past = await SeriesService.EndAsync(db, s.Provider, created.SeriesId, new EndSeriesRequest(Today.AddDays(-1)), Now, Ct);
        var missing = await SeriesService.EndAsync(db, s.Provider, created.SeriesId, new EndSeriesRequest(null), Now, Ct);
        var foreign = await SeriesService.EndAsync(db, s.OtherProvider, created.SeriesId, new EndSeriesRequest(Today), Now, Ct);
        var ended = await SeriesService.EndAsync(db, s.Provider, created.SeriesId, new EndSeriesRequest(Today), Now, Ct);

        // Assert
        past.Outcome.Should().Be(SeriesOutcome.InvalidDate);
        missing.Outcome.Should().Be(SeriesOutcome.InvalidDate);
        foreign.Outcome.Should().Be(SeriesOutcome.NotFound);
        ended.Value!.Cancelled.Should().Be(3);
        var series = await SeriesAsync(s, created.SeriesId, Ct);
        series.CancelledAt.Should().Be(Now);
        series.OccurrenceCount.Should().Be(3);
    }

    [Theory]
    [InlineData("after_email")]
    [InlineData("before_session")]
    public async Task Confirmation_WithA72HourAutoCancel_GoesOut96HoursAhead_SoTheDeadlineFallsBetween(string mode)
    {
        // Arrange: auto-cancel 72 h; the session is Tuesday 2026-11-24 14:00 (17:00 UTC).
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        await s.Db.ExecuteAsync(
            """INSERT INTO "BookingPolicies" ("Id","ProviderId","AutoCancelMode","AutoCancelHours") VALUES (gen_random_uuid(), @p, @m, 72)""",
            Ct, ("p", s.Provider), ("m", mode));
        var session = Tuesday14.AddDays(7);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, startsAt: session, count: 1));
        var sendAt = session.AddHours(-96);
        await using var db = s.CreateDbContext();

        // Act
        var sent = await NotificationWorkerSteps.SendDueAsync(db, new RecordingEmailSender(), PortalBase, sendAt, Ct, providerId: s.Provider);

        // Assert
        (await RemindersAsync(s, Ct)).Single(r => r.TemplateKey == "confirmation").ScheduledFor.Should().Be(sendAt);
        sent.Should().Be(2); // the summary and the confirmation request
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == created.FirstAppointmentId, Ct);
        confirmation.DeadlineAt.Should().Be(mode == "after_email" ? sendAt.AddHours(72) : session.AddHours(-72));
        confirmation.DeadlineAt.Should().BeAfter(sendAt).And.BeBefore(session);
    }

    [Fact]
    public async Task Summary_StatesTheSessionsCreated_AndTheDatesThatWereTaken()
    {
        // Arrange: four Tuesdays requested; the third (01/12) is taken.
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Tuesday14.AddDays(14), "scheduled", Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 4));
        var never = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, startsAt: Tuesday14.AddHours(3)));
        var sender = new RecordingEmailSender();
        await using var db = s.CreateDbContext();

        // Act
        await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: s.Provider);

        // Assert
        created.Skipped.Should().ContainSingle(x => x.SeriesIndex == 3);
        never.Skipped.Should().BeEmpty();
        sender.Sent.Should().HaveCount(2);
        sender.Sent.Should().ContainSingle(m => m.Text.Contains("toda terça às 14:00, a partir de 17/11, 4 sessões")
            && m.Text.Contains("Foram marcadas 3 das 4 sessões. Não foi possível marcar a sessão de 01/12/2026, porque o horário já estava ocupado."));
        sender.Sent.Should().ContainSingle(m => m.Text.Contains("toda terça às 17:00, a partir de 17/11 (Online)")
            && m.Text.Contains("11 sessões já estão marcadas. As seguintes entram na agenda semana a semana."));
    }

    [Fact]
    public async Task Split_RelinksACancelledWeek_SoItIsNotRecreatedAtTheNewTime()
    {
        // Arrange: six Tuesdays; the 4th (08/12) cancelled; split from the 2nd to Thursdays 10:00.
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 6));
        var old = await OccurrencesAsync(s, Ct, created.SeriesId);
        await SetStatusAsync(s, old[3].Id, "cancelled", Ct);
        await using var db = s.CreateDbContext();

        // Act
        var preview = await SeriesService.PreviewSplitAsync(db, s.Provider, created.SeriesId,
            new SplitSeriesRequest(old[1].Id, 4, "10:00", 50, "online"), Now, Ct);
        var split = await SeriesService.SplitAsync(db, s.Provider, created.SeriesId,
            new SplitSeriesRequest(old[1].Id, 4, "10:00", 50, "online"), Now, Ct);

        // Assert
        preview.Value!.Moving.Should().Be(4);
        preview.Value.Staying.Should().BeEmpty();
        split.Outcome.Should().Be(SeriesOutcome.Ok);
        var next = await OccurrencesAsync(s, Ct, split.Value!.NewSeriesId);
        next.Select(a => a.SeriesIndex).Should().BeEquivalentTo(new int?[] { 1, 2, 3, 4, 5 });
        var relinked = next.Single(a => a.Id == old[3].Id);
        relinked.Should().Match<Server.Data.Entities.Appointment>(a => a.SeriesIndex == 3 && a.Detached && a.Status == "cancelled");
        var thursdays = next.Where(a => a.Status == "scheduled")
            .Select(a => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(a.StartsAt, SaoPaulo).DateTime)).ToList();
        thursdays.Should().Equal(new DateOnly(2026, 11, 26), new DateOnly(2026, 12, 3), new DateOnly(2026, 12, 17), new DateOnly(2026, 12, 24));
        thursdays.Should().NotContain(new DateOnly(2026, 12, 10));
        (await OccurrencesAsync(s, Ct, created.SeriesId)).Select(a => a.Id).Should().Equal(old[0].Id);
    }

    // ---- Integration ----

    [Fact]
    public async Task Block_CancelAndNotify_CancelsSeriesOccurrencesLikeAnyAppointment()
    {
        // Arrange
        await using var s = await AvailabilityScene.CreateAsync(fixture, Ct);
        var created = await CreateSeriesAsync(s, Ct, Weekly(s.Patient, count: 3));
        var occurrences = await OccurrencesAsync(s, Ct, created.SeriesId);
        var block = new BlockRequest("block", occurrences[1].StartsAt.AddHours(-1), occurrences[1].EndsAt.AddHours(1), false, null,
            CancelAffected: true, ExpectedAppointmentIds: [occurrences[1].Id]);
        await using var db = s.CreateDbContext();

        // Act
        var result = await Blocks.CreateAsync(db, s.Provider, block, Now, Ct);

        // Assert
        result.Outcome.Should().Be(BlockOutcome.Ok);
        var after = await OccurrencesAsync(s, Ct, created.SeriesId);
        after.Select(a => a.Status).Should().Equal("scheduled", "cancelled", "scheduled");
        (await RemindersAsync(s, Ct)).Should().ContainSingle(r => r.TemplateKey == "cancellation" && r.AppointmentId == occurrences[1].Id);
        (await SeriesAsync(s, created.SeriesId, Ct)).CancelledAt.Should().BeNull();
    }
}
