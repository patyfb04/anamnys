using Anamnys.Server.Appointments;
using Anamnys.Server.Availability;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static Anamnys.Tests.Availability.AvailabilityScene;

namespace Anamnys.Tests.Availability;

using Appointments = Anamnys.Server.Appointments.Appointments;

// design/specs/2026-11-09-availability-design.md §4, §6: create and reschedule answer outside_availability unless
// acknowledged, the acknowledged path writes its audit entry, and status changes never run the check.
[Collection(SharedAppHostCollection.Name)]
public class AvailabilityAppointmentsTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Monday10 = new(2026, 11, 16, 13, 0, 0, TimeSpan.Zero); // 10:00 local
    private static readonly DateTimeOffset Saturday10 = new(2026, 11, 14, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Create_OutsideHours_AnswersTheReasons_ThenSavesWhenAcknowledged()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        await s.SaveWeekdaysAsync(Today, Ct);

        // Act
        AppointmentWriteResult refused, saved, inside;
        await using (var db = s.CreateDbContext())
        {
            refused = await Appointments.CreateWithReasonsAsync(db, s.Provider, new CreateAppointmentRequest(s.Patient, Saturday10, 50, "online"), Now, Ct);
        }
        await using (var db = s.CreateDbContext())
        {
            saved = await Appointments.CreateWithReasonsAsync(db, s.Provider, new CreateAppointmentRequest(s.Patient, Saturday10, 50, "online", true), Now, Ct);
        }
        await using (var db = s.CreateDbContext())
        {
            inside = await Appointments.CreateWithReasonsAsync(db, s.Provider, new CreateAppointmentRequest(s.Patient, Monday10, 50, "online"), Now, Ct);
        }

        // Assert
        refused.Outcome.Should().Be(AppointmentOutcome.OutsideAvailability);
        refused.Id.Should().BeNull();
        refused.Reasons.Should().Equal(new OutsideAvailabilityReason("outside_hours"));
        saved.Outcome.Should().Be(AppointmentOutcome.Ok);
        inside.Outcome.Should().Be(AppointmentOutcome.Ok);
        (await s.Db.CountAsync("""SELECT count(*) FROM "Appointments" WHERE "ProviderId" = @p""", Ct, ("p", s.Provider))).Should().Be(2);
        var audit = await s.AuditAsync(Ct);
        audit.Where(a => a.StartsWith("appointment_outside_availability")).Should().ContainSingle()
            .Which.Should().Be($"appointment_outside_availability appointment {saved.Id} outside_hours");
        audit.Should().NotContain(a => a.Contains("Benedita"));
    }

    [Fact]
    public async Task Reschedule_IntoABlock_AnswersTheReasons_ModalityOnlyIsNotChecked_AndStatusChangesNeverAre()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        Guid id;
        await using (var db = s.CreateDbContext())
        {
            id = (await Appointments.CreateWithReasonsAsync(db, s.Provider, new CreateAppointmentRequest(s.Patient, Monday10, 50, "online"), Now, Ct)).Id!.Value;
        }
        await s.AddBlockAsync(s.Provider, Monday10.AddDays(1).AddHours(-1), Monday10.AddDays(1).AddHours(3), Ct);
        await s.AddBlockAsync(s.Provider, Monday10.AddHours(-1), Monday10.AddHours(3), Ct); // now over the booked one too

        // Act
        AppointmentWriteResult refused, modality, acknowledged;
        AppointmentOutcome confirmed;
        await using (var db = s.CreateDbContext())
        {
            refused = await Appointments.UpdateWithReasonsAsync(db, s.Provider, id, new UpdateAppointmentRequest(Monday10.AddDays(1), 50, "online"), Now, Ct);
        }
        await using (var db = s.CreateDbContext())
        {
            modality = await Appointments.UpdateWithReasonsAsync(db, s.Provider, id, new UpdateAppointmentRequest(Monday10, 50, "presencial"), Now, Ct);
        }
        await using (var db = s.CreateDbContext())
        {
            confirmed = await Appointments.SetStatusAsync(db, s.Provider, id, new ChangeAppointmentStatusRequest("confirmed", null), Now, Ct);
        }
        await using (var db = s.CreateDbContext())
        {
            acknowledged = await Appointments.UpdateWithReasonsAsync(db, s.Provider, id, new UpdateAppointmentRequest(Monday10.AddDays(1), 50, "online", true), Now, Ct);
        }

        // Assert
        refused.Outcome.Should().Be(AppointmentOutcome.OutsideAvailability);
        refused.Reasons.Should().ContainSingle().Which.Should().Be(
            new OutsideAvailabilityReason("block", Monday10.AddDays(1).AddHours(-1), Monday10.AddDays(1).AddHours(3), BlockKind: "block"));
        modality.Outcome.Should().Be(AppointmentOutcome.Ok);
        confirmed.Should().Be(AppointmentOutcome.Ok);
        acknowledged.Outcome.Should().Be(AppointmentOutcome.Ok);
        await using var check = s.CreateDbContext();
        var row = await check.Appointments.AsNoTracking().SingleAsync(a => a.Id == id, Ct);
        row.StartsAt.Should().Be(Monday10.AddDays(1));
        row.Modality.Should().Be("online");
        (await s.AuditAsync(Ct)).Where(a => a.StartsWith("appointment_outside_availability"))
            .Should().Equal($"appointment_outside_availability appointment {id} block");
    }

    [Fact]
    public async Task Create_WithoutSavedHours_IsCheckedOnlyForBlocksAndHolidays()
    {
        // Arrange
        await using var s = await CreateAsync(fixture, Ct);
        var friday20 = new DateTimeOffset(2026, 11, 20, 13, 0, 0, TimeSpan.Zero);

        // Act
        AppointmentWriteResult saturday, holiday;
        await using (var db = s.CreateDbContext())
        {
            saturday = await Appointments.CreateWithReasonsAsync(db, s.Provider, new CreateAppointmentRequest(s.Patient, Saturday10, 50, "online"), Now, Ct);
        }
        await using (var db = s.CreateDbContext())
        {
            holiday = await Appointments.CreateWithReasonsAsync(db, s.Provider, new CreateAppointmentRequest(s.Patient, friday20, 50, "online"), Now, Ct);
        }

        // Assert
        saturday.Outcome.Should().Be(AppointmentOutcome.Ok);
        holiday.Outcome.Should().Be(AppointmentOutcome.OutsideAvailability);
        holiday.Reasons!.Select(r => (r.Kind, r.HolidayName)).Should().Equal(("holiday", "Dia Nacional de Zumbi e da Consciência Negra"));
    }
}
