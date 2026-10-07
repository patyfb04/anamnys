using Anamnys.Server.Appointments;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Appointments;

// The test namespace shares the static class's name; this alias, declared inside the namespace,
// wins over the enclosing Anamnys.Tests.Appointments lookup so `Appointments.CreateAsync(...)` resolves.
using Appointments = Anamnys.Server.Appointments.Appointments;

// Appointments against the real Postgres of the shared AppHost. Each test seeds its own
// provider, so ownership and the overlap constraint are exercised on every call.
[Collection(SharedAppHostCollection.Name)]
public class AppointmentsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Tomorrow10 = new(2026, 10, 6, 13, 0, 0, TimeSpan.Zero); // 10:00 BRT
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreateAppointmentRequest Create(Guid patientId, DateTimeOffset startsAt, int minutes = 50) =>
        new(patientId, startsAt, minutes, "online");

    [Fact]
    public async Task Create_InsertsScheduledAppointmentInPracticeZone()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var (outcome, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        var row = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
        row.EndsAt.Should().Be(Tomorrow10.AddMinutes(50));
        row.Status.Should().Be("scheduled");
        row.Timezone.Should().Be("America/Sao_Paulo");
        row.CreatedBy.Should().Be("provider");
        row.Modality.Should().Be("online");
    }

    [Fact]
    public async Task Create_ForAnotherProvidersPatient_IsNotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var foreign = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var (outcome, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(foreign, Tomorrow10), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.NotFound);
        id.Should().BeNull();
    }

    [Fact]
    public async Task Create_ForArchivedPatient_IsRejected()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", archivedAt: Now.AddDays(-1), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var (outcome, _) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.PatientArchived);
    }

    [Fact]
    public async Task Create_Overlapping_IsOverlap_ButBackToBackIsAllowed()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10, cancellationToken: Ct); // 10:00–10:50

        // Act
        (AppointmentOutcome Outcome, Guid? Id) overlapping, backToBack;
        await using (var db = seed.CreateDbContext())
        {
            overlapping = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10.AddMinutes(30)), Now, Ct);
        }
        await using (var db = seed.CreateDbContext())
        {
            backToBack = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10.AddMinutes(50)), Now, Ct);
        }

        // Assert
        overlapping.Outcome.Should().Be(AppointmentOutcome.Overlap);
        backToBack.Outcome.Should().Be(AppointmentOutcome.Ok);
    }

    [Fact]
    public async Task Cancelling_FreesTheSlot()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        Guid first;
        await using (var db = seed.CreateDbContext())
        {
            first = (await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct)).Id!.Value;
        }

        // Act
        AppointmentOutcome cancel;
        (AppointmentOutcome Outcome, Guid? Id) again;
        await using (var db = seed.CreateDbContext())
        {
            cancel = await Appointments.SetStatusAsync(db, seed.ProviderId, first, new ChangeAppointmentStatusRequest("cancelled", " Paciente pediu "), Now, Ct);
        }
        await using (var db = seed.CreateDbContext())
        {
            again = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);
        }

        // Assert
        cancel.Should().Be(AppointmentOutcome.Ok);
        again.Outcome.Should().Be(AppointmentOutcome.Ok);
        await using var check = seed.CreateDbContext();
        var row = await check.Appointments.AsNoTracking().SingleAsync(a => a.Id == first, Ct);
        row.Status.Should().Be("cancelled");
        row.CancelledAt.Should().Be(Now);
        row.CancelledBy.Should().Be("provider");
        row.CancellationReason.Should().Be("Paciente pediu");
    }

    [Fact]
    public async Task Update_ReschedulesAndChangesModality()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        Guid id;
        await using (var db = seed.CreateDbContext())
        {
            id = (await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct)).Id!.Value;
        }

        // Act
        AppointmentOutcome outcome;
        await using (var db = seed.CreateDbContext())
        {
            outcome = await Appointments.UpdateAsync(db, seed.ProviderId, id, new UpdateAppointmentRequest(Tomorrow10.AddHours(2), 30, "presencial"), Ct);
        }

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        await using var check = seed.CreateDbContext();
        var row = await check.Appointments.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
        row.StartsAt.Should().Be(Tomorrow10.AddHours(2));
        row.EndsAt.Should().Be(Tomorrow10.AddHours(2).AddMinutes(30));
        row.Modality.Should().Be("presencial");
    }

    [Fact]
    public async Task Update_OntoAnotherAppointment_IsOverlap()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10.AddHours(2), cancellationToken: Ct);
        Guid id;
        await using (var db = seed.CreateDbContext())
        {
            id = (await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct)).Id!.Value;
        }
        await using var db2 = seed.CreateDbContext();

        // Act
        var outcome = await Appointments.UpdateAsync(db2, seed.ProviderId, id, new UpdateAppointmentRequest(Tomorrow10.AddHours(2), 50, "online"), Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Overlap);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("attended")]
    public async Task Update_WhenNotScheduledOrConfirmed_IsInvalidTransition(string status)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(-1), status, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var id = await db.Appointments.Where(a => a.PatientId == patient).Select(a => a.Id).SingleAsync(Ct);

        // Act
        var outcome = await Appointments.UpdateAsync(db, seed.ProviderId, id, new UpdateAppointmentRequest(Tomorrow10, 50, "online"), Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.InvalidTransition);
    }

    [Fact]
    public async Task Attended_SetsLastVisit_AndUndoRecomputesIt()
    {
        // Arrange — an earlier attended visit two days ago, and today's appointment at 10:00 BRT.
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var earlier = Now.AddDays(-2);
        var patient = await seed.AddPatientAsync("Ana", "Silva", lastVisit: earlier, cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, earlier, "attended", cancellationToken: Ct);
        var todayStart = Now.AddHours(-2);
        await seed.AddAppointmentAsync(patient, todayStart, cancellationToken: Ct);
        Guid id;
        await using (var db = seed.CreateDbContext())
        {
            id = await db.Appointments.Where(a => a.PatientId == patient && a.StartsAt == todayStart).Select(a => a.Id).SingleAsync(Ct);
        }

        // Act + Assert
        await using (var db = seed.CreateDbContext())
        {
            (await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("attended", null), Now, Ct))
                .Should().Be(AppointmentOutcome.Ok);
        }
        (await LastVisitAsync(seed, patient)).Should().Be(todayStart);

        await using (var db = seed.CreateDbContext())
        {
            (await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("scheduled", null), Now, Ct))
                .Should().Be(AppointmentOutcome.Ok);
        }
        (await LastVisitAsync(seed, patient)).Should().Be(earlier);
    }

    [Fact]
    public async Task Attended_OnOlderAppointment_KeepsLaterLastVisit()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", lastVisit: Now.AddHours(-1), cancellationToken: Ct);
        var older = Now.AddDays(-7);
        await seed.AddAppointmentAsync(patient, older, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var id = await db.Appointments.Where(a => a.PatientId == patient).Select(a => a.Id).SingleAsync(Ct);

        // Act
        var outcome = await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("attended", null), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        await using var fresh = seed.CreateDbContext();
        (await fresh.Appointments.AsNoTracking().Where(a => a.Id == id).Select(a => a.Status).SingleAsync(Ct)).Should().Be("attended");
        (await LastVisitAsync(seed, patient)).Should().Be(Now.AddHours(-1));
    }

    [Fact]
    public async Task UndoAttended_WithNoOtherAttendedVisit_LeavesLastVisitUnchanged()
    {
        // Arrange — spec section 3: with no remaining attended visit, LastVisit is left as it was.
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var startsAt = Now.AddHours(-2);
        var patient = await seed.AddPatientAsync("Ana", "Silva", lastVisit: startsAt, cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, startsAt, "attended", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var id = await db.Appointments.Where(a => a.PatientId == patient).Select(a => a.Id).SingleAsync(Ct);

        // Act
        var outcome = await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("scheduled", null), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        (await LastVisitAsync(seed, patient)).Should().Be(startsAt);
    }

    [Theory]
    [InlineData("attended")]
    [InlineData("no_show")]
    public async Task AttendedOrNoShow_OnFutureAppointment_IsNotStarted(string status)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var id = await db.Appointments.Where(a => a.PatientId == patient).Select(a => a.Id).SingleAsync(Ct);

        // Act
        var outcome = await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest(status, null), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.NotStarted);
    }

    [Fact]
    public async Task Status_FromCancelled_IsInvalidTransition()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10, "cancelled", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var id = await db.Appointments.Where(a => a.PatientId == patient).Select(a => a.Id).SingleAsync(Ct);

        // Act
        var outcome = await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("scheduled", null), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.InvalidTransition);
    }

    [Fact]
    public async Task EveryOperation_OnAnotherProvidersAppointment_IsNotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var foreignPatient = await seed.AddPatientAsync("Ana", "Silva", providerId: other, cancellationToken: Ct);
        await seed.AddAppointmentAsync(foreignPatient, Tomorrow10, providerId: other, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var id = await db.Appointments.Where(a => a.PatientId == foreignPatient).Select(a => a.Id).SingleAsync(Ct);

        // Act + Assert
        (await Appointments.UpdateAsync(db, seed.ProviderId, id, new UpdateAppointmentRequest(Tomorrow10, 50, "online"), Ct))
            .Should().Be(AppointmentOutcome.NotFound);
        (await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("confirmed", null), Now, Ct))
            .Should().Be(AppointmentOutcome.NotFound);
        (await Appointments.ListAsync(db, seed.ProviderId, Tomorrow10.AddDays(-1), Tomorrow10.AddDays(1), [], Ct))
            .Items.Should().BeEmpty();
    }

    [Fact]
    public async Task List_ReturnsOnlyTheRangeAndRespectsStatusFilter()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10, cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10.AddHours(2), "cancelled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10.AddDays(10), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var from = Tomorrow10.AddHours(-1);
        var to = Tomorrow10.AddDays(1);

        // Act
        var all = await Appointments.ListAsync(db, seed.ProviderId, from, to, [], Ct);
        var scheduledOnly = await Appointments.ListAsync(db, seed.ProviderId, from, to, ["scheduled"], Ct);

        // Assert
        all.Items.Select(i => i.Status).Should().Equal("scheduled", "cancelled");
        all.Items[0].PatientName.Should().Be("Ana Silva");
        all.Items[0].PatientId.Should().Be(patient);
        scheduledOnly.Items.Should().ContainSingle().Which.StartsAt.Should().Be(Tomorrow10);
    }

    [Fact]
    public async Task List_AtTheSameStart_ReturnsCancelledBeforeLive()
    {
        // Arrange — a live appointment inserted first, then a cancelled one in the same slot.
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10, "scheduled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Tomorrow10, "cancelled", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await Appointments.ListAsync(db, seed.ProviderId, Tomorrow10.AddHours(-1), Tomorrow10.AddDays(1), [], Ct);

        // Assert
        result.Items.Select(i => i.Status).Should().Equal("cancelled", "scheduled");
    }

    private static async Task<DateTimeOffset?> LastVisitAsync(PatientSearchSeed seed, Guid patientId)
    {
        await using var db = seed.CreateDbContext();
        return await db.Patients.AsNoTracking().Where(p => p.Id == patientId).Select(p => p.LastVisit).SingleAsync(Ct);
    }
}
