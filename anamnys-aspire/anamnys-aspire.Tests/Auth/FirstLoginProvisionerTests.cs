using System.Security.Claims;
using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Auth;

public class FirstLoginProvisionerTests
{
    private static AnamnysDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AnamnysDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AnamnysDbContext(options);
    }

    private static ClaimsPrincipal PrincipalFor(Guid subject, string email, string name) =>
        new(new ClaimsIdentity(
        [
            new Claim("sub", subject.ToString()),
            new Claim("email", email),
            new Claim("name", name),
        ], "test"));

    [Fact]
    public async Task ProvisionAsync_WhenProviderIsUnknown_CreatesRowWithExternalSubject()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);
        var subject = Guid.NewGuid();

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "clinician@example.com", "A Clinician"),
            Realms.Providers,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Providers.SingleAsync(TestContext.Current.CancellationToken);
        row.Id.Should().Be(localId);
        row.ExternalSubject.Should().Be(subject);
        row.Email.Should().Be("clinician@example.com");
    }

    [Fact]
    public async Task ProvisionAsync_WhenCalledTwiceForSameSubject_DoesNotCreateSecondRow()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);
        var subject = Guid.NewGuid();
        var principal = PrincipalFor(subject, "clinician@example.com", "A Clinician");

        // Act
        var first = await provisioner.ProvisionAsync(principal, Realms.Providers, TestContext.Current.CancellationToken);
        var second = await provisioner.ProvisionAsync(principal, Realms.Providers, TestContext.Current.CancellationToken);

        // Assert
        second.Should().Be(first);
        (await db.Providers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientAccountDoesNotExist_ThrowsRatherThanCreating()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(Guid.NewGuid(), "nobody@example.com", "Nobody"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
