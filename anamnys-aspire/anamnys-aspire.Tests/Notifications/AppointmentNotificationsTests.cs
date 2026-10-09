using Anamnys.Server.Appointments;
using Anamnys.Server.Notifications;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notifications;

// The test namespace tree contains Anamnys.Tests.Appointments; the alias wins over it.
using Appointments = Anamnys.Server.Appointments.Appointments;

// Appointment changes queue the e-mail outbox rows in the same SaveChanges. Real Postgres;
// each test seeds its own provider.
[Collection(SharedAppHostCollection.Name)]
public class AppointmentNotificationsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Tomorrow10 = Now.AddDays(1);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreateAppointmentRequest Create(Guid patientId, DateTimeOffset startsAt) =>
        new(patientId, startsAt, 50, "online");

    [Fact]
    public async Task Create_WithEmail_QueuesConfirmationAndReminder()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var (outcome, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id, Ct);
        confirmation.ExpiresAt.Should().Be(Tomorrow10);
        confirmation.DeadlineAt.Should().BeNull();
        confirmation.ConfirmedAt.Should().BeNull();
        confirmation.ClosedAt.Should().BeNull();
        var reminders = await db.Reminders.AsNoTracking().Where(r => r.AppointmentId == id).ToListAsync(Ct);
        reminders.Should().HaveCount(2);
        reminders.Should().OnlyContain(r => r.DeliveryStatus == "pending" && r.ConfirmationId == confirmation.Id);
        reminders.Single(r => r.TemplateKey == "confirmation").ScheduledFor.Should().Be(Now);
        reminders.Single(r => r.TemplateKey == "reminder_1h").ScheduledFor.Should().Be(Tomorrow10.AddHours(-1));
    }

    [Fact]
    public async Task Create_WithoutEmail_QueuesNothing()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);

        // Assert
        (await db.AppointmentConfirmations.AsNoTracking().CountAsync(c => c.AppointmentId == id, Ct)).Should().Be(0);
        (await db.Reminders.AsNoTracking().CountAsync(r => r.AppointmentId == id, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Create_StartingWithinAnHour_QueuesNoReminder()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Now.AddMinutes(40)), Now, Ct);

        // Assert
        var reminders = await db.Reminders.AsNoTracking().Where(r => r.AppointmentId == id).ToListAsync(Ct);
        reminders.Should().ContainSingle().Which.TemplateKey.Should().Be("confirmation");
    }

    [Fact]
    public async Task Reschedule_ClosesOldConfirmation_AndQueuesNewOnes()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);
        await Appointments.SetStatusAsync(db, seed.ProviderId, id!.Value, new ChangeAppointmentStatusRequest("confirmed", null), Now, Ct);
        var later = Now.AddMinutes(5);
        var newStart = Tomorrow10.AddHours(3);

        // Act
        var outcome = await Appointments.UpdateAsync(db, seed.ProviderId, id.Value, new UpdateAppointmentRequest(newStart, 50, "online"), later, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
        appointment.Status.Should().Be("scheduled");
        var confirmations = await db.AppointmentConfirmations.AsNoTracking().Where(c => c.AppointmentId == id).ToListAsync(Ct);
        confirmations.Should().HaveCount(2);
        var fresh = confirmations.Single(c => c.ExpiresAt == newStart);
        fresh.ClosedAt.Should().BeNull();
        fresh.ConfirmedAt.Should().BeNull();
        var old = confirmations.Single(c => c.ExpiresAt == Tomorrow10);
        var reminders = await db.Reminders.AsNoTracking().Where(r => r.AppointmentId == id).ToListAsync(Ct);
        reminders.Where(r => r.ConfirmationId == old.Id).Should().OnlyContain(r => r.DeliveryStatus == "cancelled");
        var created = reminders.Where(r => r.ConfirmationId == fresh.Id).ToList();
        created.Should().HaveCount(2).And.OnlyContain(r => r.DeliveryStatus == "pending");
        created.Single(r => r.TemplateKey == "confirmation").ScheduledFor.Should().Be(later);
        created.Single(r => r.TemplateKey == "reminder_1h").ScheduledFor.Should().Be(newStart.AddHours(-1));
    }

    [Fact]
    public async Task Reschedule_OfUnconfirmedAppointment_ClosesOldConfirmation()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);
        var later = Now.AddMinutes(5);

        // Act
        await Appointments.UpdateAsync(db, seed.ProviderId, id!.Value, new UpdateAppointmentRequest(Tomorrow10.AddHours(3), 50, "online"), later, Ct);

        // Assert
        var old = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id && c.ExpiresAt == Tomorrow10, Ct);
        old.ClosedAt.Should().Be(later);
    }

    [Fact]
    public async Task Update_WithNothingChanged_KeepsConfirmedAndQueuesNothing()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);
        await Appointments.SetStatusAsync(db, seed.ProviderId, id!.Value, new ChangeAppointmentStatusRequest("confirmed", null), Now, Ct);

        // Act
        var outcome = await Appointments.UpdateAsync(db, seed.ProviderId, id.Value, new UpdateAppointmentRequest(Tomorrow10, 50, "online"), Now.AddMinutes(5), Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id, Ct)).Status.Should().Be("confirmed");
        (await db.AppointmentConfirmations.AsNoTracking().CountAsync(c => c.AppointmentId == id, Ct)).Should().Be(1);
        var reminders = await db.Reminders.AsNoTracking().Where(r => r.AppointmentId == id).ToListAsync(Ct);
        reminders.Should().HaveCount(2);
        reminders.Single(r => r.TemplateKey == "reminder_1h").DeliveryStatus.Should().Be("pending");
    }

    [Fact]
    public async Task ProviderConfirm_ClosesConfirmation_WithoutNotification()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);

        // Act
        var outcome = await Appointments.SetStatusAsync(db, seed.ProviderId, id!.Value, new ChangeAppointmentStatusRequest("confirmed", null), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id, Ct);
        confirmation.ConfirmedAt.Should().Be(Now);
        confirmation.ConfirmedBy.Should().Be("provider");
        (await db.Notifications.AsNoTracking().CountAsync(n => n.SubjectId == id, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Cancel_ClosesConfirmation_CancelsPending_AndQueuesCancellationEmail()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);
        var later = Now.AddMinutes(10);

        // Act
        var outcome = await Appointments.SetStatusAsync(db, seed.ProviderId, id!.Value, new ChangeAppointmentStatusRequest("cancelled", null), later, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id, Ct);
        confirmation.ClosedAt.Should().Be(later);
        var reminders = await db.Reminders.AsNoTracking().Where(r => r.AppointmentId == id).ToListAsync(Ct);
        reminders.Single(r => r.TemplateKey == "reminder_1h").DeliveryStatus.Should().Be("cancelled");
        var cancellation = reminders.Single(r => r.TemplateKey == "cancellation");
        cancellation.DeliveryStatus.Should().Be("pending");
        cancellation.ScheduledFor.Should().Be(later);
        cancellation.ConfirmationId.Should().BeNull();
    }

    [Fact]
    public async Task Attended_CancelsPendingReminders()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var start = Now.AddDays(-1);
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, start), Now.AddDays(-2), Ct);
        await seed.ExecuteSqlAsync(
            """UPDATE "Reminders" SET "DeliveryStatus" = 'sent', "SentAt" = @now WHERE "AppointmentId" = @id AND "TemplateKey" = 'confirmation'""",
            Ct, ("now", Now.AddDays(-2)), ("id", id!.Value));

        // Act
        var outcome = await Appointments.SetStatusAsync(db, seed.ProviderId, id.Value, new ChangeAppointmentStatusRequest("attended", null), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        var reminders = await db.Reminders.AsNoTracking().Where(r => r.AppointmentId == id).ToListAsync(Ct);
        reminders.Single(r => r.TemplateKey == "reminder_1h").DeliveryStatus.Should().Be("cancelled");
        reminders.Single(r => r.TemplateKey == "confirmation").DeliveryStatus.Should().Be("sent");
    }

    [Fact]
    public async Task OverlapOnCreate_LeavesNoNotificationRows()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (_, first) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);

        // Act
        var (outcome, second) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10.AddMinutes(10)), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Overlap);
        second.Should().BeNull();
        var providerAppointments = db.Appointments.AsNoTracking().Where(a => a.ProviderId == seed.ProviderId).Select(a => a.Id);
        (await providerAppointments.CountAsync(Ct)).Should().Be(1);
        var confirmations = await db.AppointmentConfirmations.AsNoTracking().Where(c => providerAppointments.Contains(c.AppointmentId)).ToListAsync(Ct);
        confirmations.Should().ContainSingle().Which.AppointmentId.Should().Be(first!.Value);
        (await db.Reminders.AsNoTracking().CountAsync(r => providerAppointments.Contains(r.AppointmentId), Ct)).Should().Be(2);
    }

    [Fact]
    public async Task List_ExposesDeadlineAndPatientHasEmail()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var withEmail = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        var without = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (_, idA) = await Appointments.CreateAsync(db, seed.ProviderId, Create(withEmail, Tomorrow10), Now, Ct);
        var (_, idB) = await Appointments.CreateAsync(db, seed.ProviderId, Create(without, Tomorrow10.AddHours(2)), Now, Ct);
        var deadline = Now.AddHours(1);
        await seed.ExecuteSqlAsync(
            """UPDATE "AppointmentConfirmations" SET "DeadlineAt" = @deadline WHERE "AppointmentId" = @id""",
            Ct, ("deadline", deadline), ("id", idA!.Value));

        // Act
        var response = await Appointments.ListAsync(db, seed.ProviderId, Now, Now.AddDays(3), [], Ct);

        // Assert
        var a = response.Items.Single(i => i.Id == idA);
        a.ConfirmationDeadlineAt.Should().Be(deadline);
        a.PatientHasEmail.Should().BeTrue();
        var b = response.Items.Single(i => i.Id == idB);
        b.ConfirmationDeadlineAt.Should().BeNull();
        b.PatientHasEmail.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_WithoutEmail_QueuesNoCancellationRow()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);

        // Act
        var outcome = await Appointments.SetStatusAsync(db, seed.ProviderId, id!.Value, new ChangeAppointmentStatusRequest("cancelled", null), Now.AddMinutes(5), Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        (await db.Reminders.AsNoTracking().CountAsync(r => r.AppointmentId == id, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task NoShow_CancelsPendingReminders()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Now.AddDays(-1)), Now.AddDays(-2), Ct);

        // Act
        var outcome = await Appointments.SetStatusAsync(db, seed.ProviderId, id!.Value, new ChangeAppointmentStatusRequest("no_show", null), Now, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Ok);
        var reminders = await db.Reminders.AsNoTracking().Where(r => r.AppointmentId == id).ToListAsync(Ct);
        reminders.Should().NotBeEmpty();
        reminders.Should().OnlyContain(r => r.DeliveryStatus == "cancelled");
    }

    [Fact]
    public async Task Attended_ClosesConfirmation_SoUndoCannotLeadToAutoCancel()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Now.AddDays(-1)), Now.AddDays(-2), Ct);
        await seed.ExecuteSqlAsync(
            """UPDATE "AppointmentConfirmations" SET "DeadlineAt" = @deadline WHERE "AppointmentId" = @id""",
            Ct, ("deadline", Now.AddDays(-2).AddHours(1)), ("id", id!.Value));

        // Act
        await Appointments.SetStatusAsync(db, seed.ProviderId, id.Value, new ChangeAppointmentStatusRequest("attended", null), Now, Ct);
        await Appointments.SetStatusAsync(db, seed.ProviderId, id.Value, new ChangeAppointmentStatusRequest("scheduled", null), Now.AddMinutes(1), Ct);
        var cancelled = await NotificationWorkerSteps.CancelOverdueAsync(db, Now.AddHours(1), Ct, providerId: seed.ProviderId);

        // Assert
        cancelled.Should().Be(0);
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id, Ct);
        confirmation.ClosedAt.Should().Be(Now);
        (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id, Ct)).Status.Should().Be("scheduled");
    }

    [Fact]
    public async Task Save_WhenACheckConstraintFires_ReturnsChanged()
    {
        // Arrange: a state the provider's edit could only reach after losing a race.
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, Tomorrow10), Now, Ct);
        var appointment = await db.Appointments.SingleAsync(a => a.Id == id, Ct);
        appointment.Status = "cancelled"; // without CancelledAt: Appointments_Cancel_ck

        // Act
        var outcome = await Appointments.SaveAsync(db, Ct);

        // Assert
        outcome.Should().Be(AppointmentOutcome.Changed);

        // Arrange: AppointmentConfirmations_Closed_ck
        var confirmation = await db.AppointmentConfirmations.SingleAsync(c => c.AppointmentId == id, Ct);
        confirmation.ConfirmedAt = Now;
        confirmation.ClosedAt = Now;

        // Act / Assert
        (await Appointments.SaveAsync(db, Ct)).Should().Be(AppointmentOutcome.Changed);
    }
}
