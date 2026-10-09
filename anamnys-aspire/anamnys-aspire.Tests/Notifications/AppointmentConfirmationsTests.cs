using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Email;
using Anamnys.Server.Notifications;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notifications;

// The test namespace tree contains Anamnys.Tests.Appointments; the alias wins over it.
using Appointments = Anamnys.Server.Appointments.Appointments;

// Confirmation by e-mail link and from the portal, against real Postgres with a fake clock.
// Tokens are read from the mail the worker step "sends" into a RecordingEmailSender.
[Collection(SharedAppHostCollection.Name)]
public class AppointmentConfirmationsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StartsAt = Now.AddDays(1);
    private static readonly Uri PortalBase = new("http://localhost:5274/patient");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Arranged(PatientSearchSeed Seed, AnamnysDbContext Db, Guid PatientId, Guid AppointmentId, string Token, RecordingEmailSender Sender);

    private async Task<Arranged> ArrangeAsync()
    {
        var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", email: "ana@mail.test", cancellationToken: Ct);
        var db = seed.CreateDbContext();
        var (_, id) = await Appointments.CreateAsync(db, seed.ProviderId, new CreateAppointmentRequest(patient, StartsAt, 50, "online"), Now, Ct);
        var sender = new RecordingEmailSender();
        await NotificationWorkerSteps.SendDueAsync(db, sender, PortalBase, Now, Ct, providerId: seed.ProviderId);
        return new Arranged(seed, db, patient, id!.Value, TokenFrom(sender.Sent.Single()), sender);
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

    private static Task<int> NotificationCountAsync(AnamnysDbContext db, Guid providerId, Guid appointmentId) =>
        db.Notifications.AsNoTracking().CountAsync(
            n => n.ProviderId == providerId && n.Kind == "appointment_confirmed" && n.SubjectId == appointmentId, Ct);

    [Fact]
    public async Task View_PendingToken_ShowsDetails_AndDoesNotConfirm()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;

        // Act
        var view = await AppointmentConfirmations.ViewAsync(a.Db, a.Token, Now.AddMinutes(1), Ct);

        // Assert
        view.Status.Should().Be("pending");
        view.ProviderName.Should().Be("Test Provider");
        view.StartsAt.Should().Be(StartsAt);
        view.EndsAt.Should().Be(StartsAt.AddMinutes(50));
        view.Modality.Should().Be("online");
        view.Timezone.Should().NotBeNullOrEmpty();
        (await a.Db.Appointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId, Ct)).Status.Should().Be("scheduled");
    }

    [Fact]
    public async Task Confirm_PendingToken_ConfirmsAndNotifies()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;

        // Act
        var first = await AppointmentConfirmations.ConfirmByTokenAsync(a.Db, a.Token, Now.AddMinutes(1), Ct);
        var second = await AppointmentConfirmations.ConfirmByTokenAsync(a.Db, a.Token, Now.AddMinutes(2), Ct);

        // Assert
        first.Should().Be(TokenConfirmOutcome.Confirmed);
        second.Should().Be(TokenConfirmOutcome.Confirmed);
        (await a.Db.Appointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId, Ct)).Status.Should().Be("confirmed");
        var confirmation = await a.Db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == a.AppointmentId, Ct);
        confirmation.ConfirmedBy.Should().Be("patient");
        confirmation.ConfirmedAt.Should().Be(Now.AddMinutes(1));
        (await NotificationCountAsync(a.Db, a.Seed.ProviderId, a.AppointmentId)).Should().Be(1);
        (await AppointmentConfirmations.ViewAsync(a.Db, a.Token, Now.AddMinutes(3), Ct)).Status.Should().Be("confirmed");
    }

    [Fact]
    public async Task Confirm_AfterReschedule_IsInvalid()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;
        await Appointments.UpdateAsync(a.Db, a.Seed.ProviderId, a.AppointmentId,
            new UpdateAppointmentRequest(StartsAt.AddDays(1), 50, "online"), Now.AddMinutes(5), Ct);

        // Act
        var outcome = await AppointmentConfirmations.ConfirmByTokenAsync(a.Db, a.Token, Now.AddMinutes(6), Ct);
        var view = await AppointmentConfirmations.ViewAsync(a.Db, a.Token, Now.AddMinutes(6), Ct);

        // Assert
        outcome.Should().Be(TokenConfirmOutcome.Invalid);
        view.Should().Be(new ConfirmationView("invalid", null, null, null, null, null));
    }

    [Fact]
    public async Task Confirm_AfterReschedule_OfAConfirmedAppointment_IsInvalid()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;
        (await AppointmentConfirmations.ConfirmByTokenAsync(a.Db, a.Token, Now.AddMinutes(1), Ct)).Should().Be(TokenConfirmOutcome.Confirmed);
        await Appointments.UpdateAsync(a.Db, a.Seed.ProviderId, a.AppointmentId,
            new UpdateAppointmentRequest(StartsAt.AddDays(1), 50, "online"), Now.AddMinutes(5), Ct);

        // Act
        var view = await AppointmentConfirmations.ViewAsync(a.Db, a.Token, Now.AddMinutes(6), Ct);
        var outcome = await AppointmentConfirmations.ConfirmByTokenAsync(a.Db, a.Token, Now.AddMinutes(6), Ct);

        // Assert
        view.Status.Should().Be("invalid");
        outcome.Should().Be(TokenConfirmOutcome.Invalid);
        (await a.Db.Appointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId, Ct)).Status.Should().Be("scheduled");
    }

    [Fact]
    public async Task Confirm_AfterTheWorkerCancelledIt_StaysCancelled_AndDoesNotNotify()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;
        var account = await a.Seed.BindPortalAccountAsync(a.PatientId, Ct);
        (await NotificationWorkerSteps.CancelOverdueAsync(a.Db, Now.AddMinutes(61), Ct, providerId: a.Seed.ProviderId)).Should().Be(1);
        await using var requestDb = a.Seed.CreateDbContext();

        // Act
        var byToken = await AppointmentConfirmations.ConfirmByTokenAsync(requestDb, a.Token, Now.AddMinutes(62), Ct);
        var fromPortal = await AppointmentConfirmations.ConfirmFromPortalAsync(requestDb, account, a.AppointmentId, Now.AddMinutes(62), Ct);

        // Assert
        byToken.Should().Be(TokenConfirmOutcome.Invalid);
        fromPortal.Should().Be(PortalConfirmOutcome.InvalidState);
        (await a.Db.Appointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId, Ct)).Status.Should().Be("cancelled");
        (await NotificationCountAsync(a.Db, a.Seed.ProviderId, a.AppointmentId)).Should().Be(0);
    }

    [Fact]
    public async Task Confirm_AfterCancel_IsInvalid()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;
        await Appointments.SetStatusAsync(a.Db, a.Seed.ProviderId, a.AppointmentId, new ChangeAppointmentStatusRequest("cancelled", null), Now.AddMinutes(5), Ct);

        // Act
        var outcome = await AppointmentConfirmations.ConfirmByTokenAsync(a.Db, a.Token, Now.AddMinutes(6), Ct);

        // Assert
        outcome.Should().Be(TokenConfirmOutcome.Invalid);
    }

    [Fact]
    public async Task Confirm_AfterStart_IsInvalid()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;

        // Act
        var outcome = await AppointmentConfirmations.ConfirmByTokenAsync(a.Db, a.Token, StartsAt.AddMinutes(1), Ct);

        // Assert
        outcome.Should().Be(TokenConfirmOutcome.Invalid);
        (await AppointmentConfirmations.ViewAsync(a.Db, a.Token, StartsAt.AddMinutes(1), Ct)).Status.Should().Be("invalid");
    }

    [Fact]
    public async Task Confirm_UnknownToken_IsInvalid()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;

        // Act / Assert
        (await AppointmentConfirmations.ConfirmByTokenAsync(a.Db, "not-a-token", Now, Ct)).Should().Be(TokenConfirmOutcome.Invalid);
        (await AppointmentConfirmations.ConfirmByTokenAsync(a.Db, new string('A', 43), Now, Ct)).Should().Be(TokenConfirmOutcome.Invalid);
        (await AppointmentConfirmations.ViewAsync(a.Db, new string('A', 43), Now, Ct)).Status.Should().Be("invalid");
    }

    [Fact]
    public async Task Confirm_ReminderToken_AlsoWorks()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;
        a.Sender.Sent.Clear();
        await NotificationWorkerSteps.SendDueAsync(a.Db, a.Sender, PortalBase, StartsAt.AddHours(-1), Ct, providerId: a.Seed.ProviderId);
        var reminderToken = TokenFrom(a.Sender.Sent.Single());

        // Act
        var outcome = await AppointmentConfirmations.ConfirmByTokenAsync(a.Db, reminderToken, StartsAt.AddHours(-1), Ct);

        // Assert
        reminderToken.Should().NotBe(a.Token);
        outcome.Should().Be(TokenConfirmOutcome.Confirmed);
        (await a.Db.Appointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId, Ct)).Status.Should().Be("confirmed");
    }

    [Fact]
    public async Task Portal_Confirm_LinkedAccount_Works()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;
        var account = await a.Seed.BindPortalAccountAsync(a.PatientId, Ct);

        // Act
        var outcome = await AppointmentConfirmations.ConfirmFromPortalAsync(a.Db, account, a.AppointmentId, Now.AddMinutes(1), Ct);

        // Assert
        outcome.Should().Be(PortalConfirmOutcome.Ok);
        (await a.Db.Appointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId, Ct)).Status.Should().Be("confirmed");
        (await a.Db.AppointmentConfirmations.AsNoTracking().SingleAsync(c => c.AppointmentId == a.AppointmentId, Ct))
            .ConfirmedBy.Should().Be("patient");
        (await NotificationCountAsync(a.Db, a.Seed.ProviderId, a.AppointmentId)).Should().Be(1);
    }

    [Fact]
    public async Task Portal_Confirm_UnlinkedAccount_IsNotFound()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;
        var other = await a.Seed.AddAccountAsync(Ct);

        // Act / Assert
        (await AppointmentConfirmations.ConfirmFromPortalAsync(a.Db, other, a.AppointmentId, Now, Ct)).Should().Be(PortalConfirmOutcome.NotFound);
        (await AppointmentConfirmations.ConfirmFromPortalAsync(a.Db, other, Guid.NewGuid(), Now, Ct)).Should().Be(PortalConfirmOutcome.NotFound);
        (await a.Db.Appointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId, Ct)).Status.Should().Be("scheduled");
    }

    [Fact]
    public async Task Portal_Confirm_AlreadyConfirmedOrPast_IsInvalidState()
    {
        // Arrange
        var a = await ArrangeAsync();
        await using var _s = a.Seed;
        await using var _d = a.Db;
        var account = await a.Seed.BindPortalAccountAsync(a.PatientId, Ct);

        // Act / Assert
        (await AppointmentConfirmations.ConfirmFromPortalAsync(a.Db, account, a.AppointmentId, StartsAt.AddMinutes(1), Ct))
            .Should().Be(PortalConfirmOutcome.InvalidState);
        (await AppointmentConfirmations.ConfirmFromPortalAsync(a.Db, account, a.AppointmentId, Now.AddMinutes(1), Ct))
            .Should().Be(PortalConfirmOutcome.Ok);
        (await AppointmentConfirmations.ConfirmFromPortalAsync(a.Db, account, a.AppointmentId, Now.AddMinutes(2), Ct))
            .Should().Be(PortalConfirmOutcome.InvalidState);
        (await NotificationCountAsync(a.Db, a.Seed.ProviderId, a.AppointmentId)).Should().Be(1);
    }
}
