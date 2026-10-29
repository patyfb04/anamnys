using Anamnys.Server.Notifications;
using Anamnys.Server.Retention;
using FluentAssertions;

namespace Anamnys.Tests.Retention;

// The daily job and its bell notifications (design/specs/2026-10-28-retention-panel-design.md §3, §6 "Notifications").
[Collection(SharedAppHostCollection.Name)]
public class RetentionJobTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string CountByKind = """SELECT count(*) FROM "Notifications" WHERE "ProviderId" = @p AND "Kind" = @k""";

    [Fact]
    public async Task RunAsync_NotifiesEachNewItemOnce_AndNeverRepeats()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var ana = await s.AddPatientAsync(provider, "Ana", Ct);
        var bia = await s.AddPatientAsync(provider, "Bia", Ct);
        await s.AddSessionAsync(provider, ana, new DateTimeOffset(2021, 11, 1, 15, 0, 0, TimeSpan.Zero), Ct);
        await s.AddNoteAsync(provider, bia, Now.AddDays(-8), Ct);
        await s.AddNoteAsync(provider, bia, Now.AddDays(-9), Ct);
        await s.AddNoteAsync(provider, bia, Now.AddDays(-1), Ct);
        await using var db = s.CreateDbContext();

        // Act
        var first = await RetentionJobSteps.RunAsync(db, Now, Ct, providerId: provider);
        var second = await RetentionJobSteps.RunAsync(db, Now.AddDays(1), Ct, providerId: provider);

        // Assert
        first.Should().Be(3);
        second.Should().Be(0);
        (await s.CountAsync(CountByKind, Ct, ("p", provider), ("k", RetentionJobSteps.RetentionKind))).Should().Be(1);
        (await s.CountAsync(CountByKind, Ct, ("p", provider), ("k", RetentionJobSteps.UnsignedKind))).Should().Be(2);
        (await s.RetentionOfAsync(ana, Ct)).RetentionUntil.Should().Be(new DateOnly(2026, 11, 1));
    }

    [Fact]
    public async Task RunAsync_ANewDeadline_IsANewItem()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var ana = await s.AddPatientAsync(provider, "Ana", Ct);
        await s.AddSessionAsync(provider, ana, new DateTimeOffset(2021, 11, 1, 15, 0, 0, TimeSpan.Zero), Ct);
        await using var db = s.CreateDbContext();
        await RetentionJobSteps.RunAsync(db, Now, Ct, providerId: provider);

        // Act: a new session moves the deadline; the new date is a new item, notified once.
        await s.AddSessionAsync(provider, ana, new DateTimeOffset(2021, 12, 1, 15, 0, 0, TimeSpan.Zero), Ct);
        var pushed = await RetentionJobSteps.RunAsync(db, Now, Ct, providerId: provider);
        var nextYear = await RetentionJobSteps.RunAsync(db, Now.AddDays(10), Ct, providerId: provider);

        // Assert
        pushed.Should().Be(1);
        nextYear.Should().Be(0);
        (await s.CountAsync(CountByKind, Ct, ("p", provider), ("k", RetentionJobSteps.RetentionKind))).Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_OtherProviders_AreLeftAlone_AndTheBellListsTheItems()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var other = await s.AddProviderAsync(Ct);
        var ana = await s.AddPatientAsync(provider, "Ana", Ct);
        var foreign = await s.AddPatientAsync(other, "Bia", Ct);
        await s.AddNoteAsync(provider, ana, Now.AddDays(-8), Ct);
        await s.AddNoteAsync(other, foreign, Now.AddDays(-8), Ct);
        await s.AddSessionAsync(provider, ana, new DateTimeOffset(2020, 1, 1, 15, 0, 0, TimeSpan.Zero), Ct);
        await using var db = s.CreateDbContext();

        // Act
        await RetentionJobSteps.RunAsync(db, Now, Ct, providerId: provider);
        var bell = await ProviderNotifications.ListAsync(db, provider, 1, Ct);

        // Assert
        (await s.CountAsync("""SELECT count(*) FROM "Notifications" WHERE "ProviderId" = @p""", Ct, ("p", other))).Should().Be(0);
        bell.Items.Select(i => (i.Kind, i.PatientName)).Should().BeEquivalentTo(new[]
        {
            (RetentionJobSteps.UnsignedKind, "Ana Silva"), (RetentionJobSteps.RetentionKind, "Ana Silva"),
        });
        bell.UnreadCount.Should().Be(2);
    }
}
