using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Email;
using Anamnys.Server.Notifications;
using Anamnys.Server.Patients;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Anamnys.Tests.Notifications;

// The test namespace tree contains Anamnys.Tests.Appointments; the alias wins over it.
using Appointments = Anamnys.Server.Appointments.Appointments;

// The worker's two steps against real Postgres, driven with a fake clock. Every call passes
// providerId so a test never touches another test's (or the dev data's) rows.
[Collection(SharedAppHostCollection.Name)]
public class NotificationWorkerStepsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StartsAt = Now.AddDays(1);
    private static readonly Uri PortalBase = new("http://localhost:5274/patient");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CreateAppointmentRequest Create(Guid patientId, DateTimeOffset startsAt) =>
        new(patientId, startsAt, 50, "online");

    // A provider with one patient (with e-mail) and one appointment created through the
    // real code path, so the outbox hooks have run.
    private async Task<(PatientSearchSeed Seed, AnamnysDbContext Db, Guid AppointmentId)> ArrangeAsync(DateTimeOffset? startsAt = null)
    {
        var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        var db = seed.CreateDbContext();
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient, startsAt ?? StartsAt), Now, Ct);
        return (seed, db, id!.Value);
    }

    private static string TokenFrom(EmailMessage message)
    {
        const string marker = "/patient/confirmar/";
        var text = message.Text;
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }
        return text[start..end];
    }

    [Fact]
    public async Task Send_ConfirmationEmail_SetsDeadline_AndStoresOnlyAHash()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        var sender = new RecordingEmailSender();

        // Act
        var sent = await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: seed.ProviderId);

        // Assert
        sent.Should().Be(1);
        var message = sender.Sent.Should().ContainSingle().Subject;
        message.ToEmail.Should().Be("ana@mail.test");
        message.Text.Should().Contain("/patient/confirmar/");
        var token = TokenFrom(message);
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id, Ct);
        var tokens = await db.AppointmentConfirmationTokens.AsNoTracking().Where(t => t.ConfirmationId == confirmation.Id).ToListAsync(Ct);
        tokens.Should().ContainSingle().Which.TokenHash.Should().Equal(PatientInvitationTokens.Hash(token));
        var tokenRows = await db.Database.SqlQuery<string>(
            $"""SELECT row_to_json(t)::text AS "Value" FROM "AppointmentConfirmationTokens" t WHERE t."ConfirmationId" = {confirmation.Id}""").ToListAsync(Ct);
        tokenRows.Should().OnlyContain(r => !r.Contains(token));
        var reminderRows = await db.Database.SqlQuery<string>(
            $"""SELECT row_to_json(r)::text AS "Value" FROM "Reminders" r WHERE r."AppointmentId" = {id}""").ToListAsync(Ct);
        reminderRows.Should().OnlyContain(r => !r.Contains(token));
        confirmation.DeadlineAt.Should().Be(Now.AddHours(1));
        var reminder = await db.Reminders.AsNoTracking().SingleAsync(r => r.AppointmentId == id && r.TemplateKey == "confirmation", Ct);
        reminder.DeliveryStatus.Should().Be("sent");
        reminder.SentAt.Should().Be(Now);
    }

    [Fact]
    public async Task Send_UsesProviderPolicy()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync(Now.AddDays(3));
        await using var _s = seed;
        await using var _d = db;
        await seed.ExecuteSqlAsync(
            """INSERT INTO "BookingPolicies" ("Id", "ProviderId", "AutoCancelMode", "AutoCancelHours") VALUES (gen_random_uuid(), @p, 'before_session', 24)""",
            Ct, ("p", seed.ProviderId));

        // Act
        await NotificationWorkerSteps.SendDueAsync(db, new RecordingEmailSender(), PortalBase, Now, Ct, providerId: seed.ProviderId);

        // Assert
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id, Ct);
        confirmation.DeadlineAt.Should().Be(Now.AddDays(3).AddHours(-24));
    }

    [Fact]
    public async Task Send_PolicyOff_NoDeadline()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        await seed.ExecuteSqlAsync(
            """INSERT INTO "BookingPolicies" ("Id", "ProviderId", "AutoCancelMode", "AutoCancelHours") VALUES (gen_random_uuid(), @p, 'off', 1)""",
            Ct, ("p", seed.ProviderId));

        // Act
        await NotificationWorkerSteps.SendDueAsync(db, new RecordingEmailSender(), PortalBase, Now, Ct, providerId: seed.ProviderId);

        // Assert
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id, Ct);
        confirmation.DeadlineAt.Should().BeNull();
    }

    [Fact]
    public async Task Send_Failure_RetriesThenFails()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        var sender = new RecordingEmailSender { FailNext = 3 };

        // Act
        for (var i = 0; i < 3; i++)
        {
            await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: seed.ProviderId);
        }

        // Assert
        var reminder = await db.Reminders.AsNoTracking().SingleAsync(r => r.AppointmentId == id && r.TemplateKey == "confirmation", Ct);
        reminder.DeliveryStatus.Should().Be("failed");
        reminder.Attempts.Should().Be(3);
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id, Ct);
        (await db.AppointmentConfirmationTokens.AsNoTracking().CountAsync(t => t.ConfirmationId == confirmation.Id, Ct)).Should().Be(0);
        confirmation.DeadlineAt.Should().BeNull();
        sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_FailureThenSuccess_KeepsOneAttempt()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        var sender = new RecordingEmailSender { FailNext = 1 };

        // Act
        await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: seed.ProviderId);
        await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: seed.ProviderId);

        // Assert
        var reminder = await db.Reminders.AsNoTracking().SingleAsync(r => r.AppointmentId == id && r.TemplateKey == "confirmation", Ct);
        reminder.DeliveryStatus.Should().Be("sent");
        reminder.Attempts.Should().Be(1);
        sender.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Send_UnknownTimezone_CountsAsAFailedAttempt_AndDoesNotAbortTheBatch()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        var patient2 = await seed.AddPatientAsync("Bia", "Souza", email: "bia@mail.test", cancellationToken: Ct);
        var (_, id2) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient2, StartsAt.AddDays(1)), Now, Ct);
        await seed.ExecuteSqlAsync(
            """UPDATE "Appointments" SET "Timezone" = 'Not/AZone' WHERE "Id" = @id""", Ct, ("id", id));
        var sender = new RecordingEmailSender();
        // A fresh context, as the worker has: `db` still tracks the appointment with its old Timezone.
        await using var workerDb = seed.CreateDbContext();

        // Act
        var sent = await NotificationWorkerSteps.SendDueAsync(workerDb, sender, PortalBase, Now, Ct, providerId: seed.ProviderId);

        // Assert
        sent.Should().Be(1);
        sender.Sent.Should().ContainSingle().Which.ToEmail.Should().Be("bia@mail.test");
        var bad = await db.Reminders.AsNoTracking().SingleAsync(r => r.AppointmentId == id && r.TemplateKey == "confirmation", Ct);
        bad.DeliveryStatus.Should().Be("pending");
        bad.Attempts.Should().Be(1);
        var good = await db.Reminders.AsNoTracking().SingleAsync(r => r.AppointmentId == id2 && r.TemplateKey == "confirmation", Ct);
        good.DeliveryStatus.Should().Be("sent");
    }

    [Fact]
    public async Task Send_NotYetDue_IsLeftAlone()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        var sender = new RecordingEmailSender();
        await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: seed.ProviderId);
        sender.Sent.Clear();

        // Act
        var sent = await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now.AddMinutes(10), Ct, providerId: seed.ProviderId);

        // Assert
        sent.Should().Be(0);
        sender.Sent.Should().BeEmpty();
        var reminder = await db.Reminders.AsNoTracking().SingleAsync(r => r.AppointmentId == id && r.TemplateKey == "reminder_1h", Ct);
        reminder.DeliveryStatus.Should().Be("pending");
    }

    [Fact]
    public async Task Send_ReminderForCancelledAppointment_IsCancelled()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("cancelled", null), Now, Ct);
        await seed.ExecuteSqlAsync(
            """
            INSERT INTO "Reminders" ("Id", "AppointmentId", "Channel", "TemplateKey", "ScheduledFor", "DeliveryStatus", "Attempts")
            VALUES (gen_random_uuid(), @a, 'email', 'reminder_1h', @at, 'pending', 0)
            """,
            Ct, ("a", id), ("at", Now));
        var sender = new RecordingEmailSender();

        // Act
        await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: seed.ProviderId);

        // Assert
        var reminders = await db.Reminders.AsNoTracking().Where(r => r.AppointmentId == id).ToListAsync(Ct);
        reminders.Single(r => r.TemplateKey == "reminder_1h" && r.ScheduledFor == Now)
            .DeliveryStatus.Should().Be("cancelled");
        sender.Sent.Should().ContainSingle();
        reminders.Single(r => r.TemplateKey == "cancellation").DeliveryStatus.Should().Be("sent");
    }

    [Fact]
    public async Task Send_ReminderAfterConfirmation_HasNoLink()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        var sender = new RecordingEmailSender();
        await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: seed.ProviderId);
        await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("confirmed", null), Now.AddMinutes(5), Ct);
        sender.Sent.Clear();

        // Act
        var sent = await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, StartsAt.AddHours(-1), Ct, providerId: seed.ProviderId);

        // Assert
        sent.Should().Be(1);
        sender.Sent.Single().Text.Should().NotContain("/confirmar/");
    }

    [Fact]
    public async Task CancelOverdue_CancelsBySystem_NotifiesProvider_AndFreesSlot()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        await NotificationWorkerSteps.SendDueAsync(db, new RecordingEmailSender(), PortalBase, Now, Ct, providerId: seed.ProviderId);

        // Act
        var cancelled = await NotificationWorkerSteps.CancelOverdueAsync(db, Now.AddMinutes(61), Ct, providerId: seed.ProviderId);

        // Assert
        cancelled.Should().Be(1);
        var appointment = await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
        appointment.Status.Should().Be("cancelled");
        appointment.CancelledBy.Should().Be("system");
        appointment.CancellationReason.Should().Be("Não confirmada no prazo");
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id, Ct);
        confirmation.ClosedAt.Should().NotBeNull();
        (await db.Reminders.AsNoTracking().SingleAsync(r => r.AppointmentId == id && r.TemplateKey == "cancellation", Ct))
            .DeliveryStatus.Should().Be("pending");
        (await db.Notifications.AsNoTracking().CountAsync(
            n => n.ProviderId == seed.ProviderId && n.Kind == "appointment_auto_cancelled" && n.SubjectId == id, Ct)).Should().Be(1);
        var patient2 = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        var (outcome, _) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient2, StartsAt), Now.AddMinutes(62), Ct);
        outcome.Should().Be(AppointmentOutcome.Ok);
    }

    [Fact]
    public async Task CancelOverdue_BeforeDeadline_OrConfirmed_DoesNothing()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        await NotificationWorkerSteps.SendDueAsync(db, new RecordingEmailSender(), PortalBase, Now, Ct, providerId: seed.ProviderId);

        // Act / Assert
        (await NotificationWorkerSteps.CancelOverdueAsync(db, Now.AddMinutes(59), Ct, providerId: seed.ProviderId)).Should().Be(0);
        await Appointments.SetStatusAsync(db, seed.ProviderId, id, new ChangeAppointmentStatusRequest("confirmed", null), Now.AddMinutes(30), Ct);
        (await NotificationWorkerSteps.CancelOverdueAsync(db, Now.AddHours(2), Ct, providerId: seed.ProviderId)).Should().Be(0);
    }

    [Fact]
    public async Task BothSteps_WithoutProviderFilter_RunTheProductionSql()
    {
        // Arrange: a time before any row, so nothing in the shared database matches.
        var (seed, db, _) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        var longAgo = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        var cancelled = await NotificationWorkerSteps.CancelOverdueAsync(db, longAgo, Ct);
        var sent = await NotificationWorkerSteps.SendDueAsync(db, new RecordingEmailSender(), PortalBase, longAgo, Ct);

        // Assert
        cancelled.Should().Be(0);
        sent.Should().Be(0);
    }

    [Fact]
    public async Task CancelOverdue_EmailNeverSent_DoesNothing()
    {
        // Arrange
        var (seed, db, _) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;

        // Act
        var cancelled = await NotificationWorkerSteps.CancelOverdueAsync(db, Now.AddHours(5), Ct, providerId: seed.ProviderId);

        // Assert
        cancelled.Should().Be(0);
    }

    [Fact]
    public async Task Send_UnexpectedException_CountsAsAFailedAttempt_WithoutAToken_AndNextRowIsStillSent()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        var patient2 = await seed.AddPatientAsync("Bia", "Souza", email: "bia@mail.test", cancellationToken: Ct);
        var (_, id2) = await Appointments.CreateAsync(db, seed.ProviderId, Create(patient2, StartsAt.AddDays(1)), Now.AddMinutes(1), Ct);
        var sender = new RecordingEmailSender();
        sender.ThrowNext.Enqueue(new InvalidOperationException("boom ana@mail.test"));
        var logger = new CapturingLogger();

        // Act
        var sent = await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now.AddMinutes(1), Ct, providerId: seed.ProviderId, logger: logger);

        // Assert
        sent.Should().Be(1);
        var failed = await db.Reminders.AsNoTracking().SingleAsync(r => r.AppointmentId == id && r.TemplateKey == "confirmation", Ct);
        failed.Attempts.Should().Be(1);
        failed.DeliveryStatus.Should().Be("pending");
        var confirmation = await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id, Ct);
        (await db.AppointmentConfirmationTokens.AsNoTracking().CountAsync(t => t.ConfirmationId == confirmation.Id, Ct)).Should().Be(0);
        (await db.Reminders.AsNoTracking().SingleAsync(r => r.AppointmentId == id2 && r.TemplateKey == "confirmation", Ct))
            .DeliveryStatus.Should().Be("sent");
        sender.Sent.Should().ContainSingle().Which.ToEmail.Should().Be("bia@mail.test");
        var warning = logger.Messages.Should().ContainSingle().Subject;
        warning.Should().Contain(failed.Id.ToString()).And.Contain(nameof(InvalidOperationException));
        warning.Should().NotContain("ana@mail.test").And.NotContain("boom");
    }

    [Fact]
    public async Task Send_TimeoutFromTheSender_CountsAsAFailedAttempt()
    {
        // Arrange
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        var sender = new RecordingEmailSender();
        sender.ThrowNext.Enqueue(new TaskCanceledException("timeout"));

        // Act
        var sent = await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: seed.ProviderId);

        // Assert
        sent.Should().Be(0);
        var reminder = await db.Reminders.AsNoTracking().SingleAsync(r => r.AppointmentId == id && r.TemplateKey == "confirmation", Ct);
        reminder.Attempts.Should().Be(1);
        reminder.DeliveryStatus.Should().Be("pending");
    }

    [Fact]
    public async Task CancelOverdue_SessionAlreadyStarted_IsNotCancelled()
    {
        // Arrange: an overdue deadline on a session whose start has passed (e.g. after a worker outage).
        var (seed, db, id) = await ArrangeAsync();
        await using var _s = seed;
        await using var _d = db;
        await NotificationWorkerSteps.SendDueAsync(db, new RecordingEmailSender(), PortalBase, Now, Ct, providerId: seed.ProviderId);

        // Act
        var cancelled = await NotificationWorkerSteps.CancelOverdueAsync(db, StartsAt.AddMinutes(10), Ct, providerId: seed.ProviderId);

        // Assert
        cancelled.Should().Be(0);
        (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id, Ct)).Status.Should().Be("scheduled");
    }

    [Fact]
    public async Task Send_SessionStartingWithin30Minutes_GetsNoDeadline_AndIsNeverCancelled()
    {
        // Arrange: the default rule (1 h after the e-mail) would fall after the start.
        var (seed, db, id) = await ArrangeAsync(Now.AddMinutes(30));
        await using var _s = seed;
        await using var _d = db;
        await NotificationWorkerSteps.SendDueAsync(db, new RecordingEmailSender(), PortalBase, Now, Ct, providerId: seed.ProviderId);

        // Act
        var cancelled = await NotificationWorkerSteps.CancelOverdueAsync(db, Now.AddHours(3), Ct, providerId: seed.ProviderId);

        // Assert
        (await db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == id, Ct)).DeadlineAt.Should().BeNull();
        cancelled.Should().Be(0);
        (await db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id, Ct)).Status.Should().Be("scheduled");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
