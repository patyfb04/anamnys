using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notifications;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Notifications;

// Provider in-app notification list and the auto-cancel policy. Real Postgres; each test seeds its own provider.
[Collection(SharedAppHostCollection.Name)]
public class ProviderNotificationsTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> AddAppointmentAsync(PatientSearchSeed seed, Guid patientId, DateTimeOffset startsAt, Guid? providerId = null)
    {
        await seed.AddAppointmentAsync(patientId, startsAt, providerId: providerId, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        return await db.Appointments.AsNoTracking()
            .Where(a => a.PatientId == patientId && a.StartsAt == startsAt)
            .Select(a => a.Id).SingleAsync(Ct);
    }

    private static async Task<Guid> AddNotificationAsync(
        PatientSearchSeed seed, Guid providerId, Guid appointmentId, DateTimeOffset scheduledFor,
        string channel = "in_app", string kind = NotificationKinds.Confirmed)
    {
        await using var db = seed.CreateDbContext();
        var id = Guid.NewGuid();
        db.Notifications.Add(new Notification
        {
            Id = id, ProviderId = providerId, Kind = kind, SubjectType = "appointment", SubjectId = appointmentId,
            Channel = channel, ScheduledFor = scheduledFor, SentAt = scheduledFor, DeliveryStatus = "sent",
        });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    [Fact]
    public async Task List_ReturnsOwnInAppNewestFirstWithPatientAndStart()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var ana = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var bia = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        var otherPatient = await seed.AddPatientAsync("Caio", "Lima", providerId: other, cancellationToken: Ct);
        var start1 = Now.AddDays(1);
        var start2 = Now.AddDays(2);
        var a1 = await AddAppointmentAsync(seed, ana, start1);
        var a2 = await AddAppointmentAsync(seed, bia, start2);
        var a3 = await AddAppointmentAsync(seed, otherPatient, start1, other);
        var older = await AddNotificationAsync(seed, seed.ProviderId, a1, Now.AddHours(-2));
        var newer = await AddNotificationAsync(seed, seed.ProviderId, a2, Now.AddHours(-1), kind: NotificationKinds.AutoCancelled);
        await AddNotificationAsync(seed, seed.ProviderId, a1, Now, channel: "email");
        await AddNotificationAsync(seed, other, a3, Now);
        await using var db = seed.CreateDbContext();

        // Act
        var page = await ProviderNotifications.ListAsync(db, seed.ProviderId, 1, Ct);

        // Assert
        page.Items.Select(i => i.Id).Should().Equal(newer, older);
        page.Items[0].Should().Match<NotificationItem>(i =>
            i.Kind == NotificationKinds.AutoCancelled && i.AppointmentId == a2 && i.PatientName == "Bia Souza"
            && i.StartsAt == start2 && i.CreatedAt == Now.AddHours(-1) && i.ReadAt == null);
        page.Items[1].PatientName.Should().Be("Ana Silva");
        page.UnreadCount.Should().Be(2);
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
    }

    [Fact]
    public async Task List_PagesTwentyAtATime_UnreadCountCoversAllPages()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var ana = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointment = await AddAppointmentAsync(seed, ana, Now.AddDays(1));
        await using (var write = seed.CreateDbContext())
        {
            for (var i = 0; i < 25; i++)
            {
                write.Notifications.Add(new Notification
                {
                    Id = Guid.NewGuid(), ProviderId = seed.ProviderId, Kind = NotificationKinds.Confirmed,
                    SubjectType = "appointment", SubjectId = appointment, ScheduledFor = Now.AddMinutes(-i), SentAt = Now,
                });
            }
            await write.SaveChangesAsync(Ct);
        }
        await using var db = seed.CreateDbContext();

        // Act
        var first = await ProviderNotifications.ListAsync(db, seed.ProviderId, 1, Ct);
        var second = await ProviderNotifications.ListAsync(db, seed.ProviderId, 2, Ct);

        // Assert
        first.Items.Should().HaveCount(20);
        second.Items.Should().HaveCount(5);
        second.UnreadCount.Should().Be(25);
        second.Page.Should().Be(2);
    }

    [Fact]
    public async Task MarkRead_OwnNotification_SetsReadAtAndLowersUnread()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var ana = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var appointment = await AddAppointmentAsync(seed, ana, Now.AddDays(1));
        var id = await AddNotificationAsync(seed, seed.ProviderId, appointment, Now);
        await AddNotificationAsync(seed, seed.ProviderId, appointment, Now.AddMinutes(-1));
        await using var db = seed.CreateDbContext();

        // Act
        var ok = await ProviderNotifications.MarkReadAsync(db, seed.ProviderId, id, Now, Ct);
        var page = await ProviderNotifications.ListAsync(db, seed.ProviderId, 1, Ct);

        // Assert
        ok.Should().BeTrue();
        page.UnreadCount.Should().Be(1);
        page.Items.Single(i => i.Id == id).ReadAt.Should().Be(Now);
    }

    [Fact]
    public async Task MarkRead_AnotherProvidersNotification_ReturnsFalseAndLeavesItUnread()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Caio", "Lima", providerId: other, cancellationToken: Ct);
        var appointment = await AddAppointmentAsync(seed, patient, Now.AddDays(1), other);
        var id = await AddNotificationAsync(seed, other, appointment, Now);
        await using var db = seed.CreateDbContext();

        // Act
        var ok = await ProviderNotifications.MarkReadAsync(db, seed.ProviderId, id, Now, Ct);

        // Assert
        ok.Should().BeFalse();
        (await ProviderNotifications.ListAsync(db, other, 1, Ct)).UnreadCount.Should().Be(1);
    }

    [Fact]
    public async Task MarkAllRead_MarksOnlyThisProvidersUnread()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var ana = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var caio = await seed.AddPatientAsync("Caio", "Lima", providerId: other, cancellationToken: Ct);
        var a1 = await AddAppointmentAsync(seed, ana, Now.AddDays(1));
        var a2 = await AddAppointmentAsync(seed, caio, Now.AddDays(1), other);
        await AddNotificationAsync(seed, seed.ProviderId, a1, Now);
        await AddNotificationAsync(seed, seed.ProviderId, a1, Now.AddMinutes(-1));
        await AddNotificationAsync(seed, other, a2, Now);
        await using var db = seed.CreateDbContext();

        // Act
        await ProviderNotifications.MarkAllReadAsync(db, seed.ProviderId, Now, Ct);

        // Assert
        (await ProviderNotifications.ListAsync(db, seed.ProviderId, 1, Ct)).UnreadCount.Should().Be(0);
        (await ProviderNotifications.ListAsync(db, other, 1, Ct)).UnreadCount.Should().Be(1);
    }

    [Fact]
    public async Task BookingPolicy_WithoutRow_ReturnsDefaults()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var policy = await BookingPolicies.GetAsync(db, seed.ProviderId, Ct);

        // Assert
        policy.Should().Be(new BookingPolicyDto("after_email", 1));
    }

    [Fact]
    public async Task BookingPolicy_SaveThenSaveAgain_UpdatesTheSameRow()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();

        // Act
        await BookingPolicies.SaveAsync(db, seed.ProviderId, new BookingPolicyRequest("before_session", 24), Ct);
        var first = await BookingPolicies.GetAsync(db, seed.ProviderId, Ct);
        await BookingPolicies.SaveAsync(db, seed.ProviderId, new BookingPolicyRequest("off", 2), Ct);
        var second = await BookingPolicies.GetAsync(db, seed.ProviderId, Ct);

        // Assert
        first.Should().Be(new BookingPolicyDto("before_session", 24));
        second.Should().Be(new BookingPolicyDto("off", 2));
        (await db.BookingPolicies.AsNoTracking().CountAsync(p => p.ProviderId == seed.ProviderId, Ct)).Should().Be(1);
    }

    [Theory]
    [InlineData("x", 1, "autoCancelMode")]
    [InlineData(null, 1, "autoCancelMode")]
    [InlineData("off", 0, "autoCancelHours")]
    [InlineData("off", 169, "autoCancelHours")]
    [InlineData("off", null, "autoCancelHours")]
    public void Validate_RejectsInvalidValues(string? mode, int? hours, string key)
    {
        // Act
        var errors = new BookingPolicyRequest(mode, hours).Validate();

        // Assert
        errors.Should().ContainKey(key);
    }

    [Theory]
    [InlineData("off", 1)]
    [InlineData("after_email", 168)]
    public void Validate_AcceptsBoundaries(string mode, int hours) =>
        new BookingPolicyRequest(mode, hours).Validate().Should().BeEmpty();
}
