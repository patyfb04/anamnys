using Anamnys.Server.Sharing;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Anamnys.Tests.Sharing;

public class ShareContractsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    private static RecordAuthorizationRequest Valid() =>
        new("Dr. Pedro Lima", "Psiquiatra", "Continuidade do cuidado", Now.AddDays(-1), "signed_term", true);

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(30, true)]
    [InlineData(0, false)]
    [InlineData(31, false)]
    public void CreateShareLinkRequest_Validate_BoundsTheExpiry(int? days, bool valid)
    {
        // Act
        var errors = new CreateShareLinkRequest(Guid.NewGuid(), days, null, null).Validate();

        // Assert
        errors.ContainsKey("expiresInDays").Should().Be(!valid);
    }

    [Fact]
    public void CreateShareLinkRequest_Validate_RefusesACapBelowOne()
    {
        // Act
        var errors = new CreateShareLinkRequest(Guid.NewGuid(), null, 0, null).Validate();

        // Assert
        errors.Should().ContainKey("viewCap");
    }

    [Fact]
    public void RecordAuthorizationRequest_Validate_AcceptsACompleteRecord()
    {
        // Act
        var errors = Valid().Validate(Now);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void RecordAuthorizationRequest_Validate_RequiresEveryFieldAndTheConfirmation()
    {
        // Arrange
        var request = new RecordAuthorizationRequest(" ", "", null, Now.AddDays(1), "by_telepathy", false);

        // Act
        var errors = request.Validate(Now);

        // Assert
        errors.Keys.Should().BeEquivalentTo("recipientName", "recipientRole", "purpose", "authorizedAt", "obtainedVia", "confirmed");
    }

    [Theory]
    [InlineData("Dr. Pedro\nLima")]
    [InlineData("Dr. Pedro\tLima")]
    [InlineData("Dr. Pedro\u2028Lima")]
    [InlineData("Dr. Pedro\u2029Lima")]
    [InlineData("Dr. Pedro‮Lima")]
    [InlineData("Dr. Pedro​Lima")]
    public void RecordAuthorizationRequest_Validate_RefusesControlAndInvisibleCharactersInTheRecipient(string name)
    {
        // Arrange
        var request = Valid() with { RecipientName = name, RecipientRole = name };

        // Act
        var errors = request.Validate(Now);

        // Assert
        errors.Keys.Should().BeEquivalentTo("recipientName", "recipientRole");
    }

    [Fact]
    public void RecordAuthorizationRequest_Validate_KeepsAccentsInTheName()
    {
        // Arrange
        var request = Valid() with { RecipientName = "Dra. Conceição d'Ávila" };

        // Act
        var errors = request.Validate(Now);

        // Assert
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/s/abc", true)]
    [InlineData("/patient/s/abc", true)]
    [InlineData("/patient/agenda", false)]
    [InlineData("/api/public/shares/open", false)]
    public void IsTokenPagePath_FindsThePagesWhosePathHoldsTheToken(string path, bool expected)
    {
        // Act
        var found = ShareEndpoints.IsTokenPagePath(new PathString(path));

        // Assert
        found.Should().Be(expected);
    }
}
