using Anamnys.Server.Appointments;
using FluentAssertions;

namespace Anamnys.Tests.Appointments;

public class AppointmentValidationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ValidRequest_HasNoErrors()
    {
        new CreateAppointmentRequest(Guid.NewGuid(), Start, 50, "online").Validate().Should().BeEmpty();
    }

    [Fact]
    public void Create_MissingFields_ReportsEachField()
    {
        new CreateAppointmentRequest(null, null, null, null).Validate().Keys
            .Should().BeEquivalentTo(["patientId", "startsAt", "durationMinutes", "modality"]);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(481)]
    public void Create_DurationOutOfBounds_IsInvalid(int minutes)
    {
        new CreateAppointmentRequest(Guid.NewGuid(), Start, minutes, "online").Validate().Keys.Should().Equal("durationMinutes");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(480)]
    public void Create_DurationAtBounds_IsValid(int minutes)
    {
        new CreateAppointmentRequest(Guid.NewGuid(), Start, minutes, "presencial").Validate().Should().BeEmpty();
    }

    [Fact]
    public void Create_UnknownModality_IsInvalid()
    {
        new CreateAppointmentRequest(Guid.NewGuid(), Start, 50, "telefone").Validate().Keys.Should().Equal("modality");
    }

    [Fact]
    public void Update_DoesNotRequirePatient()
    {
        new UpdateAppointmentRequest(Start, 50, "online").Validate().Should().BeEmpty();
    }

    [Fact]
    public void Status_Unknown_IsInvalid()
    {
        new ChangeAppointmentStatusRequest("done", null).Validate().Keys.Should().Equal("status");
    }

    [Fact]
    public void Status_ReasonWithoutCancel_IsInvalid()
    {
        new ChangeAppointmentStatusRequest("confirmed", "motivo").Validate().Keys.Should().Equal("reason");
    }

    [Fact]
    public void Status_ReasonTooLong_IsInvalid()
    {
        new ChangeAppointmentStatusRequest("cancelled", new string('x', 501)).Validate().Keys.Should().Equal("reason");
    }

    [Fact]
    public void Status_CancelWithReason_IsValid()
    {
        new ChangeAppointmentStatusRequest("cancelled", "Paciente pediu").Validate().Should().BeEmpty();
    }

    [Fact]
    public void Range_Valid_HasNoErrors()
    {
        AppointmentRange.Validate(Start, Start.AddDays(7), ["scheduled"]).Should().BeEmpty();
    }

    [Fact]
    public void Range_Missing_ReportsBoth()
    {
        AppointmentRange.Validate(null, null, null).Keys.Should().BeEquivalentTo(["from", "to"]);
    }

    [Fact]
    public void Range_ToNotAfterFrom_IsInvalid()
    {
        AppointmentRange.Validate(Start, Start, null).Keys.Should().Equal("to");
    }

    [Fact]
    public void Range_LongerThan42Days_IsInvalid()
    {
        AppointmentRange.Validate(Start, Start.AddDays(43), null).Keys.Should().Equal("to");
    }

    [Fact]
    public void Range_UnknownStatus_IsInvalid()
    {
        AppointmentRange.Validate(Start, Start.AddDays(1), ["done"]).Keys.Should().Equal("status");
    }

    [Theory]
    [InlineData("scheduled", "confirmed", true)]
    [InlineData("scheduled", "attended", true)]
    [InlineData("scheduled", "no_show", true)]
    [InlineData("scheduled", "cancelled", true)]
    [InlineData("scheduled", "scheduled", false)]
    [InlineData("confirmed", "scheduled", true)]
    [InlineData("confirmed", "attended", true)]
    [InlineData("confirmed", "no_show", true)]
    [InlineData("confirmed", "cancelled", true)]
    [InlineData("attended", "scheduled", true)]
    [InlineData("attended", "cancelled", false)]
    [InlineData("attended", "no_show", false)]
    [InlineData("no_show", "scheduled", true)]
    [InlineData("no_show", "attended", false)]
    [InlineData("cancelled", "scheduled", false)]
    [InlineData("cancelled", "confirmed", false)]
    public void Transitions_FollowTheSpecTable(string from, string to, bool allowed)
    {
        AppointmentTransitions.IsAllowed(from, to).Should().Be(allowed);
    }
}
