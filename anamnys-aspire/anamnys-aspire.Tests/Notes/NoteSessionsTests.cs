using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notes;

[Collection(SharedAppHostCollection.Name)]
public class NoteSessionsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AttendAsync_StartedScheduledAppointment_CreatesAttendedSessionLinksAndMarksAttended()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-2), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        Session? session = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            var appointment = await NoteSessions.LockAppointmentAsync(db, seed.ProviderId, patient, appointmentId, Ct);
            session = await NoteSessions.AttendAsync(db, seed.ProviderId, patient, appointment, null, null, Now, Ct);
            await db.SaveChangesAsync(Ct);
            await tx.CommitAsync(Ct);
        });

        // Assert
        await using var check = seed.CreateDbContext();
        var row = await check.Sessions.AsNoTracking().SingleAsync(s => s.Id == session!.Id, Ct);
        row.Attendance.Should().Be("attended");
        row.ScheduledStart.Should().Be(Now.AddHours(-2));
        row.ScheduledMinutes.Should().Be(50);
        row.ActualStart.Should().BeNull();
        var saved = await check.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId, Ct);
        saved.SessionId.Should().Be(session!.Id);
        saved.Status.Should().Be("attended");
        (await check.Patients.AsNoTracking().SingleAsync(p => p.Id == patient, Ct)).LastVisit.Should().Be(Now.AddHours(-2));
    }

    [Fact]
    public async Task AttendAsync_FutureAppointment_LinksSessionButKeepsStatus()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(2), "confirmed", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var appointment = await NoteSessions.LockAppointmentAsync(db, seed.ProviderId, patient, appointmentId, Ct);
        await NoteSessions.AttendAsync(db, seed.ProviderId, patient, appointment, null, null, Now, Ct);
        await db.SaveChangesAsync(Ct);

        // Assert
        var saved = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId, Ct);
        saved.Status.Should().Be("confirmed");
        saved.SessionId.Should().NotBeNull();
    }

    [Fact]
    public async Task AttendAsync_AppointmentWithSession_ReusesIt()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-2), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var first = await NoteSessions.AttendAsync(
            db, seed.ProviderId, patient, await NoteSessions.LockAppointmentAsync(db, seed.ProviderId, patient, appointmentId, Ct), null, null, Now, Ct);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        // Act
        var second = await NoteSessions.AttendAsync(
            db, seed.ProviderId, patient, await NoteSessions.LockAppointmentAsync(db, seed.ProviderId, patient, appointmentId, Ct), null, null, Now, Ct);
        await db.SaveChangesAsync(Ct);

        // Assert
        second.Id.Should().Be(first.Id);
        (await db.Sessions.AsNoTracking().CountAsync(s => s.PatientId == patient, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task AttendAsync_NoAppointment_CreatesStandaloneSession()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var session = await NoteSessions.AttendAsync(db, seed.ProviderId, patient, null, Now.AddMinutes(-30), Now, Now, Ct);
        await db.SaveChangesAsync(Ct);

        // Assert
        var row = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == session!.Id, Ct);
        row.ScheduledStart.Should().BeNull();
        row.ActualStart.Should().Be(Now.AddMinutes(-30));
        row.ActualEnd.Should().Be(Now);
    }

    [Fact]
    public async Task LockAppointmentAsync_OtherPatientsAppointment_ReturnsNull()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var other = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(other, Now.AddHours(-2), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var appointment = await NoteSessions.LockAppointmentAsync(db, seed.ProviderId, patient, appointmentId, Ct);

        // Assert
        appointment.Should().BeNull();
    }

    [Theory]
    [InlineData("cancelled", false, false)]
    [InlineData("scheduled", true, false)]
    [InlineData("scheduled", false, true)]
    public async Task IsAvailableForNoteAsync_StatusAndNote_MatchesRule(string status, bool withNote, bool expected)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointmentId = await seed.AddAppointmentAsync(patient, Now.AddHours(-2), status, cancellationToken: Ct);
        if (withNote)
        {
            await seed.ExecuteSqlAsync(
                """
                WITH s AS (INSERT INTO "Sessions" ("PatientId", "ProviderId", "Attendance") VALUES (@patient, @provider, 'attended') RETURNING "Id"),
                     a AS (UPDATE "Appointments" SET "SessionId" = (SELECT "Id" FROM s) WHERE "Id" = @appointment)
                INSERT INTO "Notes" ("ProviderId", "PatientId", "SessionId", "Status", "InputMode") SELECT @provider, @patient, "Id", 'ReadyForReview', 'Text' FROM s
                """,
                Ct, ("patient", patient), ("provider", seed.ProviderId), ("appointment", appointmentId));
        }
        await using var db = seed.CreateDbContext();
        var appointment = await db.Appointments.SingleAsync(a => a.Id == appointmentId, Ct);

        // Act
        var available = await NoteSessions.IsAvailableForNoteAsync(db, appointment, Ct);

        // Assert
        available.Should().Be(expected);
    }
}
