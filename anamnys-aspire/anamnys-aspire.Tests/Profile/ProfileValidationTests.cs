using Anamnys.Server.Profile;
using FluentAssertions;

namespace Anamnys.Tests.Profile;

public class ProfileValidationTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);

    [Fact]
    public void Validate_WhenProviderRequestIsComplete_ReturnsNoErrors()
    {
        // Arrange
        var request = new UpdateProviderProfileRequest("Ana Souza", "06/12345", "SP");

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenProviderHasNoCrp_ReturnsNoErrors()
    {
        // Arrange
        var request = new UpdateProviderProfileRequest("Ana Souza", null, " ");

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Validate_WhenProviderNameIsBlank_ReportsName(string? name)
    {
        // Arrange
        var request = new UpdateProviderProfileRequest(name, null, null);

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey("name");
    }

    [Fact]
    public void Validate_WhenProviderNameIsTooLong_ReportsName()
    {
        // Arrange
        var request = new UpdateProviderProfileRequest(new string('a', 256), null, null);

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey("name");
    }

    [Fact]
    public void Validate_WhenOnlyCrpNumberIsSet_ReportsCrpRegion()
    {
        // Arrange
        var request = new UpdateProviderProfileRequest("Ana Souza", "06/12345", null);

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey("crpRegion");
    }

    [Fact]
    public void Validate_WhenCrpFieldsAreTooLong_ReportsBoth()
    {
        // Arrange
        var request = new UpdateProviderProfileRequest("Ana Souza", new string('1', 21), new string('S', 11));

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKeys("crpNumber", "crpRegion");
    }

    [Fact]
    public void Validate_WhenPatientRequestIsComplete_ReturnsNoErrors()
    {
        // Arrange
        var request = new UpdatePatientProfileRequest("Ana", "Souza", "+55 (11) 91234-5678", new DateOnly(1990, 1, 2));

        // Act
        var errors = request.Validate(Today);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenPatientNamesAreBlank_ReportsBoth()
    {
        // Arrange
        var request = new UpdatePatientProfileRequest(" ", null, null, null);

        // Act
        var errors = request.Validate(Today);

        // Assert
        errors.Should().ContainKeys("firstName", "lastName");
    }

    [Theory]
    [InlineData("11 9abc-1234")]
    [InlineData("1234567890123456789012345678901")]
    public void Validate_WhenPatientPhoneIsInvalid_ReportsPhone(string phone)
    {
        // Arrange
        var request = new UpdatePatientProfileRequest("Ana", "Souza", phone, null);

        // Act
        var errors = request.Validate(Today);

        // Assert
        errors.Should().ContainKey("phone");
    }

    [Fact]
    public void Validate_WhenPatientBirthDateIsInTheFuture_ReportsDateOfBirth()
    {
        // Arrange
        var request = new UpdatePatientProfileRequest("Ana", "Souza", null, Today.AddDays(1));

        // Act
        var errors = request.Validate(Today);

        // Assert
        errors.Should().ContainKey("dateOfBirth");
    }
}
