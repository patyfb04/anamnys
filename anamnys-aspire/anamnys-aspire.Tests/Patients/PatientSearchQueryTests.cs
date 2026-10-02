using Anamnys.Server.Patients;
using FluentAssertions;

namespace Anamnys.Tests.Patients;

// Runs PatientSearchQuery against the real Postgres of the shared AppHost with a fixed
// clock. Each test seeds its own provider (see PatientSearchSeed), which both isolates the
// tests and exercises provider scoping on every query.
[Collection(SharedAppHostCollection.Name)]
public class PatientSearchQueryTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scoping_OtherProvidersRowsNeverAppear()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var own = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var otherProvider = await seed.AddProviderAsync(Ct);
        var foreign = await seed.AddPatientAsync("Ana", "Silva", providerId: otherProvider, cancellationToken: Ct);
        await seed.AddAppointmentAsync(foreign, Now.AddDays(1), providerId: otherProvider, cancellationToken: Ct);
        await seed.AddNoteAsync(foreign, "Draft", Now.AddDays(-1), providerId: otherProvider, cancellationToken: Ct);

        // Act
        var result = await SearchAsync(seed, new PatientSearchRequest());

        // Assert
        result.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Id.Should().Be(own);
    }

    [Fact]
    public async Task Archived_PatientsHiddenUnlessRequested()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var active = await seed.AddPatientAsync("Ana", "Ativa", cancellationToken: Ct);
        var archived = await seed.AddPatientAsync("Bia", "Arquivada", archivedAt: Now.AddDays(-1), cancellationToken: Ct);

        // Act
        var defaultResult = await SearchAsync(seed, new PatientSearchRequest());
        var archivedResult = await SearchAsync(seed, new PatientSearchRequest { Archived = true });

        // Assert
        defaultResult.Items.Select(i => i.Id).Should().Equal(active);
        defaultResult.TotalCount.Should().Be(1);
        archivedResult.Items.Select(i => i.Id).Should().Equal(archived);
    }

    [Fact]
    public async Task NextAppointment_IsEarliestFutureScheduledOrConfirmed()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(-1), "scheduled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(1), "cancelled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(1).AddHours(2), "attended", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(1).AddHours(4), "no_show", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(5), "scheduled", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(2), "confirmed", cancellationToken: Ct);

        // Act
        var result = await SearchAsync(seed, new PatientSearchRequest());

        // Assert
        result.Items.Single().NextAppointmentAt.Should().Be(Now.AddDays(2));
    }

    [Fact]
    public async Task NextAppointment_IsNullWithoutAFutureOne()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await seed.AddAppointmentAsync(patient, Now.AddDays(-3), cancellationToken: Ct);

        // Act
        var result = await SearchAsync(seed, new PatientSearchRequest());

        // Assert
        result.Items.Single().NextAppointmentAt.Should().BeNull();
    }

    [Fact]
    public async Task NoteStatus_GroupsTheMostRecentNote()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var review = await seed.AddPatientAsync("A", "Review", cancellationToken: Ct);
        await seed.AddNoteAsync(review, "ReadyForReview", Now.AddDays(-1), cancellationToken: Ct);
        var exported = await seed.AddPatientAsync("B", "Exported", cancellationToken: Ct);
        await seed.AddNoteAsync(exported, "Exported", Now.AddDays(-1), cancellationToken: Ct);
        var noNotes = await seed.AddPatientAsync("C", "NoNotes", cancellationToken: Ct);
        var draftAfterSigned = await seed.AddPatientAsync("D", "DraftAfterSigned", cancellationToken: Ct);
        await seed.AddNoteAsync(draftAfterSigned, "Signed", Now.AddDays(-10), cancellationToken: Ct);
        await seed.AddNoteAsync(draftAfterSigned, "Draft", Now.AddDays(-1), cancellationToken: Ct);

        // Act
        var result = await SearchAsync(seed, new PatientSearchRequest());

        // Assert
        var byId = result.Items.ToDictionary(i => i.Id, i => i.NoteStatus);
        byId[review].Should().Be(NoteStatusGroup.Pending);
        byId[exported].Should().Be(NoteStatusGroup.Signed);
        byId[noNotes].Should().Be(NoteStatusGroup.None);
        byId[draftAfterSigned].Should().Be(NoteStatusGroup.Pending);
    }

    [Fact]
    public async Task Search_MatchesFullNameOrEmail_CaseInsensitive()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var byName = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var byEmail = await seed.AddPatientAsync("Bruno", "Costa", email: $"bruno.{Guid.NewGuid():N}@clinic.test", cancellationToken: Ct);
        await seed.AddPatientAsync("Carla", "Dias", email: $"a100b.{Guid.NewGuid():N}@mail.test", cancellationToken: Ct);

        // Act
        var nameResult = await SearchAsync(seed, new PatientSearchRequest { Search = "ana silva" });
        var emailResult = await SearchAsync(seed, new PatientSearchRequest { Search = "@CLINIC" });
        var wildcardResult = await SearchAsync(seed, new PatientSearchRequest { Search = "100%" });

        // Assert
        nameResult.Items.Select(i => i.Id).Should().Equal(byName);
        emailResult.Items.Select(i => i.Id).Should().Equal(byEmail);
        wildcardResult.Items.Should().BeEmpty("% in the search text is a literal, not a wildcard");
    }

    [Fact]
    public async Task NameAndEmailFilters_MatchOnlyTheirField()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var silva = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var silvaEmail = await seed.AddPatientAsync("Bruno", "Costa", email: $"silva.{Guid.NewGuid():N}@mail.test", cancellationToken: Ct);

        // Act
        var byName = await SearchAsync(seed, new PatientSearchRequest { Name = "SILVA" });
        var byEmail = await SearchAsync(seed, new PatientSearchRequest { Email = "silva" });

        // Assert
        byName.Items.Select(i => i.Id).Should().Equal(silva);
        byEmail.Items.Select(i => i.Id).Should().Equal(silvaEmail);
    }

    [Fact]
    public async Task NoteStatusFilter_CombinesValuesWithOr()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var pending = await seed.AddPatientAsync("A", "Pending", cancellationToken: Ct);
        await seed.AddNoteAsync(pending, "Draft", Now.AddDays(-1), cancellationToken: Ct);
        var signed = await seed.AddPatientAsync("B", "Signed", cancellationToken: Ct);
        await seed.AddNoteAsync(signed, "Signed", Now.AddDays(-1), cancellationToken: Ct);
        var none = await seed.AddPatientAsync("C", "None", cancellationToken: Ct);

        // Act
        var result = await SearchAsync(seed, new PatientSearchRequest
        {
            NoteStatus = [NoteStatusGroup.Signed, NoteStatusGroup.None],
        });

        // Assert
        result.Items.Select(i => i.Id).Should().BeEquivalentTo([signed, none]);
    }

    [Fact]
    public async Task LastVisitRange_IncludesBothBoundaryDaysInSaoPaulo()
    {
        // Arrange — São Paulo is UTC-3, so local midnight is 03:00Z.
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await seed.AddPatientAsync("A", "BeforeFrom", lastVisit: new DateTimeOffset(2026, 9, 10, 2, 59, 0, TimeSpan.Zero), cancellationToken: Ct);
        var atFrom = await seed.AddPatientAsync("B", "AtFrom", lastVisit: new DateTimeOffset(2026, 9, 10, 3, 0, 0, TimeSpan.Zero), cancellationToken: Ct);
        var endOfTo = await seed.AddPatientAsync("C", "EndOfTo", lastVisit: new DateTimeOffset(2026, 9, 21, 2, 59, 0, TimeSpan.Zero), cancellationToken: Ct);
        await seed.AddPatientAsync("D", "AfterTo", lastVisit: new DateTimeOffset(2026, 9, 21, 3, 0, 0, TimeSpan.Zero), cancellationToken: Ct);
        await seed.AddPatientAsync("E", "NoVisit", cancellationToken: Ct);

        // Act
        var result = await SearchAsync(seed, new PatientSearchRequest
        {
            LastVisitFrom = new DateOnly(2026, 9, 10),
            LastVisitTo = new DateOnly(2026, 9, 20),
        });

        // Assert
        result.Items.Select(i => i.Id).Should().BeEquivalentTo([atFrom, endOfTo]);
    }

    [Fact]
    public async Task NextVisitRange_FromOnlyAndToOnly()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var early = await seed.AddPatientAsync("A", "Early", cancellationToken: Ct);
        await seed.AddAppointmentAsync(early, new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), cancellationToken: Ct);
        var late = await seed.AddPatientAsync("B", "Late", cancellationToken: Ct);
        await seed.AddAppointmentAsync(late, new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero), cancellationToken: Ct);
        await seed.AddPatientAsync("C", "NoAppointment", cancellationToken: Ct);

        // Act
        var fromOnly = await SearchAsync(seed, new PatientSearchRequest { NextVisitFrom = new DateOnly(2026, 10, 5) });
        var toOnly = await SearchAsync(seed, new PatientSearchRequest { NextVisitTo = new DateOnly(2026, 10, 5) });

        // Assert
        fromOnly.Items.Select(i => i.Id).Should().Equal(late);
        toOnly.Items.Select(i => i.Id).Should().Equal(early);
    }

    [Fact]
    public async Task Sort_DefaultIsNextVisitAscWithNullsLast()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var inThreeDays = await seed.AddPatientAsync("A", "Three", cancellationToken: Ct);
        await seed.AddAppointmentAsync(inThreeDays, Now.AddDays(3), cancellationToken: Ct);
        var none = await seed.AddPatientAsync("B", "None", cancellationToken: Ct);
        var tomorrow = await seed.AddPatientAsync("C", "Tomorrow", cancellationToken: Ct);
        await seed.AddAppointmentAsync(tomorrow, Now.AddDays(1), cancellationToken: Ct);

        // Act
        var result = await SearchAsync(seed, new PatientSearchRequest());

        // Assert
        result.Items.Select(i => i.Id).Should().Equal(tomorrow, inThreeDays, none);
    }

    // Alves: last 09-01, next +1d, pending. Borges: last 09-05, no next, signed.
    // Costa: no last visit, next +3d, no notes.
    [Theory]
    [InlineData("name", "asc", "Alves,Borges,Costa")]
    [InlineData("name", "desc", "Costa,Borges,Alves")]
    [InlineData("lastVisit", "asc", "Alves,Borges,Costa")]
    [InlineData("lastVisit", "desc", "Borges,Alves,Costa")]
    [InlineData("nextVisit", "asc", "Alves,Costa,Borges")]
    [InlineData("nextVisit", "desc", "Costa,Alves,Borges")]
    [InlineData("noteStatus", "asc", "Alves,Borges,Costa")]
    [InlineData("noteStatus", "desc", "Costa,Borges,Alves")]
    public async Task Sort_EachColumnBothDirections_NullsLast(string sortBy, string sortDir, string expected)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var alves = await seed.AddPatientAsync("Ana", "Alves", lastVisit: new DateTimeOffset(2026, 9, 1, 15, 0, 0, TimeSpan.Zero), cancellationToken: Ct);
        await seed.AddAppointmentAsync(alves, Now.AddDays(1), cancellationToken: Ct);
        await seed.AddNoteAsync(alves, "Draft", Now.AddDays(-30), cancellationToken: Ct);
        var borges = await seed.AddPatientAsync("Bia", "Borges", lastVisit: new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.Zero), cancellationToken: Ct);
        await seed.AddNoteAsync(borges, "Signed", Now.AddDays(-26), cancellationToken: Ct);
        var costa = await seed.AddPatientAsync("Caio", "Costa", cancellationToken: Ct);
        await seed.AddAppointmentAsync(costa, Now.AddDays(3), cancellationToken: Ct);

        // Act
        var result = await SearchAsync(seed, new PatientSearchRequest { SortBy = sortBy, SortDir = sortDir });

        // Assert
        string.Join(",", result.Items.Select(i => i.LastName)).Should().Be(expected);
    }

    [Fact]
    public async Task Sort_TiesBreakByLastNameFirstNameId()
    {
        // Arrange — nobody has an appointment, so the default sort key ties for everyone.
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var bia = await seed.AddPatientAsync("Bia", "Souza", cancellationToken: Ct);
        var ana1 = await seed.AddPatientAsync("Ana", "Souza", cancellationToken: Ct);
        var caio = await seed.AddPatientAsync("Caio", "Alves", cancellationToken: Ct);
        var ana2 = await seed.AddPatientAsync("Ana", "Souza", cancellationToken: Ct);

        // Act
        var result = await SearchAsync(seed, new PatientSearchRequest());

        // Assert — Postgres orders uuid by its bytes, which matches the hex string order.
        var anas = new[] { ana1, ana2 }.OrderBy(id => id.ToString(), StringComparer.Ordinal);
        result.Items.Select(i => i.Id).Should().Equal([caio, .. anas, bia]);
    }

    [Fact]
    public async Task Paging_CountsAllMatchesAndSlices()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        foreach (var lastName in new[] { "A", "B", "C", "D", "E" })
        {
            await seed.AddPatientAsync("X", lastName, cancellationToken: Ct);
        }

        // Act
        var page1 = await SearchAsync(seed, new PatientSearchRequest { SortBy = "name", PageSize = 2, Page = 1 });
        var page3 = await SearchAsync(seed, new PatientSearchRequest { SortBy = "name", PageSize = 2, Page = 3 });
        var page4 = await SearchAsync(seed, new PatientSearchRequest { SortBy = "name", PageSize = 2, Page = 4 });

        // Assert
        page1.Items.Select(i => i.LastName).Should().Equal("A", "B");
        page1.TotalCount.Should().Be(5);
        page3.Items.Select(i => i.LastName).Should().Equal("E");
        page4.Items.Should().BeEmpty();
        page4.TotalCount.Should().Be(5);
    }

    private static async Task<PatientSearchResponse> SearchAsync(PatientSearchSeed seed, PatientSearchRequest request)
    {
        await using var db = seed.CreateDbContext();
        return await PatientSearchQuery.ExecuteAsync(db, seed.ProviderId, Now, request, Ct);
    }
}
