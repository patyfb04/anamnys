using Anamnys.Server.Availability;
using FluentAssertions;
using static Anamnys.Tests.Availability.AvailabilityScene;

namespace Anamnys.Tests.Availability;

// design/specs/2026-11-09-availability-design.md §3, §4, §6: the preview lists only the active appointments a block
// overlaps; cancelAffected cancels them with the reason and the notice queued; without it nothing changes; deleting a
// block leaves appointments alone.
[Collection(SharedAppHostCollection.Name)]
public class BlocksTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Monday10 = new(2026, 11, 16, 13, 0, 0, TimeSpan.Zero); // 10:00 local

    private sealed record Booked(Guid Scheduled, Guid Confirmed, Guid Cancelled, Guid Attended, Guid Outside, Guid Foreign);

    private static async Task<Booked> BookAsync(AvailabilityScene s)
    {
        var scheduled = await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Monday10, "scheduled", Ct);
        var confirmed = await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Monday10.AddDays(1), "confirmed", Ct);
        var cancelled = await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Monday10.AddDays(2), "cancelled", Ct);
        var attended = await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Monday10.AddDays(3), "attended", Ct);
        var outside = await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Monday10.AddDays(10), "scheduled", Ct);
        var foreign = await s.Db.AddAppointmentAsync(s.OtherProvider, s.Stranger, Monday10, "scheduled", Ct);
        return new Booked(scheduled, confirmed, cancelled, attended, outside, foreign);
    }

    // Monday 16/11 to Friday 20/11, all day.
    private static BlockRequest Week(bool cancelAffected = false, IReadOnlyList<Guid>? expected = null) =>
        new("vacation", Monday10, Monday10.AddDays(4), AllDay: true, Reason: "Viagem particular", CancelAffected: cancelAffected,
            ExpectedAppointmentIds: expected);

    private static async Task<string> StatusAsync(AvailabilityScene s, Guid id) =>
        await s.Db.CountAsync("""SELECT count(*) FROM "Appointments" WHERE "Id" = @id AND "Status" = 'cancelled'""", Ct, ("id", id)) == 1
            ? "cancelled"
            : "active";

    [Fact]
    public async Task Preview_ListsOnlyTheActiveOverlappingAppointmentsOfTheProvider()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        var booked = await BookAsync(s);
        await using var db = s.CreateDbContext();

        // Act
        var (outcome, items) = await Blocks.PreviewAsync(db, s.Provider, Week(), Now, Ct);

        // Assert
        outcome.Should().Be(BlockOutcome.Ok);
        items!.Select(i => i.Id).Should().Equal(booked.Scheduled, booked.Confirmed);
        items![0].PatientName.Should().Be("Benedita Silva");
        items[0].PatientId.Should().Be(s.Patient);
        items[0].EndsAt.Should().Be(Monday10.AddMinutes(50));
    }

    [Fact]
    public async Task Create_WithoutCancelAffected_ChangesNoAppointment()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        var booked = await BookAsync(s);
        await using var db = s.CreateDbContext();

        // Act
        var result = await Blocks.CreateAsync(db, s.Provider, Week(), Now, Ct);

        // Assert
        result.Outcome.Should().Be(BlockOutcome.Ok);
        result.CancelledAppointmentIds.Should().BeEmpty();
        (await s.Db.CountAsync("""SELECT count(*) FROM "Appointments" WHERE "Status" = 'cancelled'""", Ct)).Should().Be(1);
        (await s.Db.CountAsync("""SELECT count(*) FROM "Reminders" """, Ct)).Should().Be(0);
        (await s.Db.CountAsync(
            """
            SELECT count(*) FROM "AvailabilityExceptions" WHERE "Id" = @id AND "AllDay" AND "Kind" = 'vacation' AND "Reason" = 'Viagem particular'
               AND "StartsAt" = '2026-11-16T03:00:00Z' AND "EndsAt" = '2026-11-21T03:00:00Z'
            """,
            Ct, ("id", result.Id!.Value))).Should().Be(1);
        var audit = await s.AuditAsync(Ct);
        audit.Should().ContainSingle().Which.Should().StartWith($"block_created block {result.Id} vacation").And.EndWith("cancelled 0");
        audit.Should().NotContain(a => a.Contains("Viagem"));
        booked.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_WithCancelAffected_CancelsTheOverlappedActiveOnesWithReasonAndNotice()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        var booked = await BookAsync(s);
        await using var db = s.CreateDbContext();

        // Act
        var result = await Blocks.CreateAsync(db, s.Provider, Week(cancelAffected: true, [booked.Confirmed, booked.Scheduled]), Now, Ct);

        // Assert
        result.Outcome.Should().Be(BlockOutcome.Ok);
        result.CancelledAppointmentIds.Should().Equal(booked.Scheduled, booked.Confirmed);
        (await s.Db.CountAsync(
            """
            SELECT count(*) FROM "Appointments"
             WHERE "Id" = ANY(@ids) AND "Status" = 'cancelled' AND "CancelledBy" = 'provider'
               AND "CancellationReason" = 'ausência do profissional' AND "CancelledAt" = @now
            """, Ct, ("ids", new[] { booked.Scheduled, booked.Confirmed }), ("now", Now))).Should().Be(2);
        (await s.Db.CountAsync(
            """SELECT count(*) FROM "Reminders" WHERE "AppointmentId" = ANY(@ids) AND "TemplateKey" = 'cancellation'""",
            Ct, ("ids", new[] { booked.Scheduled, booked.Confirmed }))).Should().Be(2);
        foreach (var untouched in new[] { booked.Attended, booked.Outside, booked.Foreign })
        {
            (await StatusAsync(s, untouched)).Should().Be("active");
        }
        (await s.AuditAsync(Ct)).Should().ContainSingle().Which.Should().EndWith("cancelled 2");
    }

    [Fact]
    public async Task Create_WhenAnAppointmentWasBookedAfterThePreview_IsChangedAndCancelsNothing()
    {
        // Arrange: the provider previewed and saw two appointments; then a third one is booked into the period.
        await using var s = await CreateAsync(fixture, Ct);
        var booked = await BookAsync(s);
        IReadOnlyList<AffectedAppointment> seen;
        await using (var db = s.CreateDbContext())
        {
            seen = (await Blocks.PreviewAsync(db, s.Provider, Week(), Now, Ct)).Items!;
        }
        var late = await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Monday10.AddDays(4), "scheduled", Ct);

        // Act
        BlockResult result;
        await using (var db = s.CreateDbContext())
        {
            result = await Blocks.CreateAsync(db, s.Provider, Week(cancelAffected: true, [.. seen.Select(a => a.Id)]), Now, Ct);
        }

        // Assert
        seen.Select(a => a.Id).Should().Equal(booked.Scheduled, booked.Confirmed);
        result.Outcome.Should().Be(BlockOutcome.Changed);
        foreach (var id in new[] { booked.Scheduled, booked.Confirmed, late })
        {
            (await StatusAsync(s, id)).Should().Be("active");
        }
        (await s.Db.CountAsync("""SELECT count(*) FROM "AvailabilityExceptions" """, Ct)).Should().Be(0);
        (await s.Db.CountAsync("""SELECT count(*) FROM "Reminders" """, Ct)).Should().Be(0);
        (await s.AuditAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_CancelAffectedWithoutExpectedIds_IsRefused()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        var booked = await BookAsync(s);
        await using var db = s.CreateDbContext();

        // Act
        var result = await Blocks.CreateAsync(db, s.Provider, Week(cancelAffected: true), Now, Ct);

        // Assert
        result.Outcome.Should().Be(BlockOutcome.ExpectedRequired);
        (await StatusAsync(s, booked.Scheduled)).Should().Be("active");
        (await s.Db.CountAsync("""SELECT count(*) FROM "AvailabilityExceptions" """, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Preview_LeavesOutAppointmentsAlreadyOver()
    {
        // Arrange: a block for today (09/11) while this morning's appointment is still "scheduled"; it is 12:00 now.
        await using var s = await CreateAsync(fixture, Ct);
        var morning = await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Now.AddHours(-3), "scheduled", Ct);
        var afternoon = await s.Db.AddAppointmentAsync(s.Provider, s.Patient, Now.AddHours(3), "scheduled", Ct);
        await using var db = s.CreateDbContext();

        // Act
        var (_, items) = await Blocks.PreviewAsync(db, s.Provider, new BlockRequest("block", Now, Now, AllDay: true), Now, Ct);

        // Assert
        items!.Select(i => i.Id).Should().Equal(afternoon);
        morning.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Delete_RemovesTheBlockOnly_AndAnotherProvidersBlockIsNotFound()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        var booked = await BookAsync(s);
        Guid id;
        await using (var db = s.CreateDbContext())
        {
            id = (await Blocks.CreateAsync(db, s.Provider, Week(), Now, Ct)).Id!.Value;
        }
        var foreign = await s.AddBlockAsync(s.OtherProvider, Monday10, Monday10.AddHours(1), Ct);

        // Act
        BlockOutcome deleted, again, theirs;
        await using (var db = s.CreateDbContext())
        {
            deleted = await Blocks.DeleteAsync(db, s.Provider, id, Now, Ct);
            again = await Blocks.DeleteAsync(db, s.Provider, id, Now, Ct);
            theirs = await Blocks.DeleteAsync(db, s.Provider, foreign, Now, Ct);
        }

        // Assert
        deleted.Should().Be(BlockOutcome.Ok);
        again.Should().Be(BlockOutcome.NotFound);
        theirs.Should().Be(BlockOutcome.NotFound);
        (await s.Db.CountAsync("""SELECT count(*) FROM "AvailabilityExceptions" """, Ct)).Should().Be(1);
        (await StatusAsync(s, booked.Scheduled)).Should().Be("active");
        (await s.AuditAsync(Ct)).Should().Contain($"block_deleted block {id} vacation");
    }
}
