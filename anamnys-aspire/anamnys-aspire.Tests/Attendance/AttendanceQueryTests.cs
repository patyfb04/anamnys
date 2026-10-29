using Anamnys.Server.Attendance;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Medications;
using Anamnys.Tests.Patients;
using FluentAssertions;

namespace Anamnys.Tests.Attendance;

// Real database (design/specs/2026-10-27-attendance-stats-design.md §6).
[Collection(SharedAppHostCollection.Name)]
public class AttendanceQueryTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 15, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Appointment> AppointmentAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset startsAt, string status,
        string? cancelledBy = null, DateTimeOffset? cancelledAt = null, Guid? sessionId = null)
    {
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(), ProviderId = providerId, PatientId = patientId, StartsAt = startsAt, EndsAt = startsAt.AddMinutes(50),
            Timezone = "America/Sao_Paulo", Modality = "online", Status = status, SessionId = sessionId,
            CancelledAt = cancelledAt, CancelledBy = cancelledBy, CreatedAt = startsAt.AddDays(-7),
        };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(Ct);
        return appointment;
    }

    private static async Task<Session> SessionAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset? actualStart, DateTimeOffset createdAt, string attendance = "attended")
    {
        var session = new Session
        {
            Id = Guid.NewGuid(), ProviderId = providerId, PatientId = patientId, ActualStart = actualStart, CreatedAt = createdAt, Attendance = attendance,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(Ct);
        return session;
    }

    [Fact]
    public async Task SummaryAsync_MixesAppointmentsAndSessions_CountingALinkedSessionOnce()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var p = seed.ProviderId;
        var linked = await SessionAsync(db, p, patient, new DateTimeOffset(2026, 9, 1, 13, 0, 0, TimeSpan.Zero), Now);
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 9, 1, 13, 0, 0, TimeSpan.Zero), "attended", sessionId: linked.Id);
        await SessionAsync(db, p, patient, null, new DateTimeOffset(2026, 9, 11, 13, 0, 0, TimeSpan.Zero));
        await SessionAsync(db, p, patient, null, new DateTimeOffset(2026, 9, 12, 13, 0, 0, TimeSpan.Zero), attendance: "scheduled");
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 9, 15, 13, 0, 0, TimeSpan.Zero), "no_show");
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 9, 22, 13, 0, 0, TimeSpan.Zero), "cancelled", "patient", new DateTimeOffset(2026, 9, 21, 13, 0, 0, TimeSpan.Zero));
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 9, 29, 13, 0, 0, TimeSpan.Zero), "cancelled", "system", new DateTimeOffset(2026, 9, 29, 7, 0, 0, TimeSpan.Zero));
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 10, 6, 13, 0, 0, TimeSpan.Zero), "confirmed");
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 10, 27, 13, 0, 0, TimeSpan.Zero), "scheduled");

        // Act
        var summary = await AttendanceQuery.SummaryAsync(db, p, patient, null, null, Now, Ct);

        // Assert
        summary!.Counts.Should().Be(new AttendanceCounts(1, 1, 1, 1, 1, 1));
        summary.IntervalDays.Should().Be(new AttendanceIntervals(10, 10, 10, 10));
        summary.CancellationLeadHours.Should().Be(new CancellationLead(15, 6, 24, 2));
        summary.DaysSinceLast.Should().Be(45);
    }

    [Fact]
    public async Task SummaryAsync_SessionWithoutActualStart_UsesCreatedAt()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        await SessionAsync(db, seed.ProviderId, patient, null, new DateTimeOffset(2026, 10, 20, 13, 0, 0, TimeSpan.Zero));

        var summary = await AttendanceQuery.SummaryAsync(db, seed.ProviderId, patient, null, null, Now, Ct);

        summary!.DaysSinceLast.Should().Be(6);
    }

    [Fact]
    public async Task SummaryAsync_WithAPeriod_FiltersOnThePracticeLocalDay()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var p = seed.ProviderId;
        // 01:30 UTC on 1 Sep is still 31 Aug at the practice.
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 9, 1, 1, 30, 0, TimeSpan.Zero), "attended");
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 9, 10, 13, 0, 0, TimeSpan.Zero), "attended");
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 10, 10, 13, 0, 0, TimeSpan.Zero), "attended");

        var summary = await AttendanceQuery.SummaryAsync(db, p, patient, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), Now, Ct);

        summary!.Counts.Attended.Should().Be(1);
    }

    [Fact]
    public async Task SummaryAsync_ScheduledAppointmentWhoseSessionWasAttended_CountsAsAttended()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var p = seed.ProviderId;
        var attended = await SessionAsync(db, p, patient, null, new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero));
        var planned = await SessionAsync(db, p, patient, null, new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero), attendance: "scheduled");
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), "scheduled", sessionId: attended.Id);
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero), "confirmed", sessionId: planned.Id);

        var summary = await AttendanceQuery.SummaryAsync(db, p, patient, null, null, Now, Ct);

        summary!.Counts.Should().Be(new AttendanceCounts(1, 0, 0, 0, 0, 1));
    }

    [Fact]
    public async Task SummaryAsync_DaysSinceLast_IgnoresFrom_ButStopsAtTo()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var p = seed.ProviderId;
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 3, 10, 13, 0, 0, TimeSpan.Zero), "attended");
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 10, 20, 13, 0, 0, TimeSpan.Zero), "attended");

        var fromJune = await AttendanceQuery.SummaryAsync(db, p, patient, new DateOnly(2026, 6, 1), null, Now, Ct);
        var upToAugust = await AttendanceQuery.SummaryAsync(db, p, patient, new DateOnly(2026, 1, 1), new DateOnly(2026, 8, 31), Now, Ct);

        fromJune!.Counts.Attended.Should().Be(1);
        fromJune.IntervalDays.Should().BeNull();
        fromJune.DaysSinceLast.Should().Be(6);
        upToAugust!.DaysSinceLast.Should().Be(230);
    }

    [Fact]
    public async Task SummaryAsync_AnotherProvidersPatient_ReturnsNull_AndNeverMixesPatients()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var neighbour = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        await AppointmentAsync(db, seed.ProviderId, neighbour, new DateTimeOffset(2026, 9, 10, 13, 0, 0, TimeSpan.Zero), "attended");
        await SessionAsync(db, seed.ProviderId, neighbour, null, new DateTimeOffset(2026, 9, 11, 13, 0, 0, TimeSpan.Zero));
        var otherProvider = await seed.AddProviderAsync(Ct);

        var foreign = await AttendanceQuery.SummaryAsync(db, otherProvider, patient, null, null, Now, Ct);
        var own = await AttendanceQuery.SummaryAsync(db, seed.ProviderId, patient, null, null, Now, Ct);

        foreign.Should().BeNull();
        own!.Counts.Should().Be(new AttendanceCounts(0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task Timeline_HasAnAppointmentsLane_WithPastEventsOnly_AndThePeriod()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var p = seed.ProviderId;
        var old = await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 3, 10, 13, 0, 0, TimeSpan.Zero), "attended");
        var attended = await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 9, 10, 13, 0, 0, TimeSpan.Zero), "attended");
        var missed = await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 9, 17, 13, 0, 0, TimeSpan.Zero), "no_show");
        var session = await SessionAsync(db, p, patient, new DateTimeOffset(2026, 10, 1, 13, 0, 0, TimeSpan.Zero), Now);
        await AppointmentAsync(db, p, patient, new DateTimeOffset(2026, 11, 3, 13, 0, 0, TimeSpan.Zero), "scheduled");

        var all = await MedicationTimeline.GetAsync(db, p, patient, null, null, Ct, Now);
        var period = await MedicationTimeline.GetAsync(db, p, patient, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 26), Ct, Now);

        all!.Appointments.Select(a => (a.Id, a.Kind)).Should().Equal(
            (old.Id, "attended"), (attended.Id, "attended"), (missed.Id, "no_show"), (session.Id, "attended_unscheduled"));
        period!.Appointments.Select(a => a.Id).Should().Equal(attended.Id, missed.Id, session.Id);
        period.Appointments[0].On.Should().Be(new DateOnly(2026, 9, 10));
    }
}
