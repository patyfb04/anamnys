using Anamnys.Server.PatientPortal;
using Anamnys.Server.Patients;
using Anamnys.Tests.Patients;
using FluentAssertions;

namespace Anamnys.Tests.PatientPortal;

// What a portal account sees, against the real Postgres. See
// design/specs/2026-10-04-patient-portal-sessions-design.md §5.
[Collection(SharedAppHostCollection.Name)]
public class PatientPortalQueriesTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Providers_ListsOnlyLinkedRecords_WithTheirNextSession()
    {
        // Arrange — the account is linked to this provider's record and to another
        // provider's; a third provider's record of the same person is not linked.
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var account = await seed.AddAccountAsync(Ct);
        var mine = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.LinkAccountAsync(mine, account, Ct);
        await seed.AddAppointmentAsync(mine, Now.AddDays(-1), "attended", cancellationToken: Ct);
        await seed.AddAppointmentAsync(mine, Now.AddDays(1), "cancelled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(mine, Now.AddDays(1).AddHours(2), "no_show", cancellationToken: Ct);
        await seed.AddAppointmentAsync(mine, Now.AddDays(3), "confirmed", cancellationToken: Ct);
        await seed.AddAppointmentAsync(mine, Now.AddDays(5), "scheduled", cancellationToken: Ct);

        var second = await seed.AddProviderAsync(Ct);
        var theirs = await seed.AddPatientAsync("Ana", "Silva", providerId: second, cancellationToken: Ct);
        await seed.LinkAccountAsync(theirs, account, Ct);

        var third = await seed.AddProviderAsync(Ct);
        await seed.AddPatientAsync("Ana", "Silva", providerId: third, cancellationToken: Ct);

        await using var db = seed.CreateDbContext();

        // Act
        var providers = await PatientPortalQueries.ProvidersAsync(db, account, Now, Ct);

        // Assert
        providers.Select(p => p.ProviderId).Should().BeEquivalentTo([seed.ProviderId, second]);
        var withSession = providers.Single(p => p.ProviderId == seed.ProviderId);
        withSession.Name.Should().Be("Test Provider");
        withSession.NextSession!.StartsAt.Should().Be(Now.AddDays(3));
        withSession.NextSession.Status.Should().Be("confirmed");
        withSession.NextSession.EndsAt.Should().Be(Now.AddDays(3).AddMinutes(50));
        providers.Single(p => p.ProviderId == second).NextSession.Should().BeNull();
    }

    [Fact]
    public async Task Providers_ForAnotherAccount_IsEmpty_AndRemovingAccessHidesTheProvider()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var account = await seed.AddAccountAsync(Ct);
        var other = await seed.AddAccountAsync(Ct);
        var record = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.LinkAccountAsync(record, account, Ct);
        await using var db = seed.CreateDbContext();

        // Act + Assert
        (await PatientPortalQueries.ProvidersAsync(db, other, Now, Ct)).Should().BeEmpty();
        (await PatientPortalQueries.ProvidersAsync(db, account, Now, Ct)).Should().ContainSingle();
        await PatientInvitations.RemoveAccessAsync(db, seed.ProviderId, record, Ct);
        (await PatientPortalQueries.ProvidersAsync(db, account, Now, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Providers_StillListsAnArchivedRecord()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var account = await seed.AddAccountAsync(Ct);
        var record = await seed.AddPatientAsync("Ana", "Silva", archivedAt: Now.AddDays(-1), cancellationToken: Ct);
        await seed.LinkAccountAsync(record, account, Ct);
        await using var db = seed.CreateDbContext();

        (await PatientPortalQueries.ProvidersAsync(db, account, Now, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Sessions_SplitsUpcomingAndPast_OrdersAndPages()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var account = await seed.AddAccountAsync(Ct);
        var record = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.LinkAccountAsync(record, account, Ct);
        await seed.AddAppointmentAsync(record, Now.AddDays(-10), "attended", cancellationToken: Ct);
        await seed.AddAppointmentAsync(record, Now.AddDays(-3), "no_show", cancellationToken: Ct);
        await seed.AddAppointmentAsync(record, Now.AddDays(-1), "cancelled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(record, Now.AddDays(2), "cancelled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(record, Now.AddDays(7), "scheduled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(record, Now.AddDays(4), "confirmed", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var upcoming = await PatientPortalQueries.SessionsAsync(db, account, seed.ProviderId, SessionScope.Upcoming, 1, Now, Ct);
        var past = await PatientPortalQueries.SessionsAsync(db, account, seed.ProviderId, SessionScope.Past, 1, Now, Ct, pageSize: 3);
        var pastPage2 = await PatientPortalQueries.SessionsAsync(db, account, seed.ProviderId, SessionScope.Past, 2, Now, Ct, pageSize: 3);

        // Assert
        upcoming!.Items.Select(s => s.StartsAt).Should().Equal(Now.AddDays(4), Now.AddDays(7));
        past!.TotalCount.Should().Be(4, "a future cancellation belongs to the history too");
        past.Items.Select(s => s.StartsAt).Should().Equal(Now.AddDays(2), Now.AddDays(-1), Now.AddDays(-3));
        past.Items.Select(s => s.Status).Should().Equal("cancelled", "cancelled", "no_show");
        pastPage2!.Items.Select(s => s.Status).Should().Equal("attended");
        upcoming.Items[0].Timezone.Should().Be("America/Sao_Paulo");
        upcoming.Items[0].Modality.Should().Be("online");
    }

    [Fact]
    public async Task Sessions_ForAnUnlinkedOrForeignProvider_IsNull()
    {
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var account = await seed.AddAccountAsync(Ct);
        var other = await seed.AddAccountAsync(Ct);
        var record = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.LinkAccountAsync(record, account, Ct);
        await seed.AddAppointmentAsync(record, Now.AddDays(1), cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        (await PatientPortalQueries.SessionsAsync(db, other, seed.ProviderId, SessionScope.Upcoming, 1, Now, Ct)).Should().BeNull();
        (await PatientPortalQueries.SessionsAsync(db, account, Guid.NewGuid(), SessionScope.Upcoming, 1, Now, Ct)).Should().BeNull();
    }
}
