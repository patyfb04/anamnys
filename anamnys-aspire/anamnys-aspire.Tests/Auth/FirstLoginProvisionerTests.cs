using System.Security.Claims;
using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
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

    private static ClaimsPrincipal PrincipalFor(
        Guid subject,
        string email,
        string name,
        bool emailVerified = true,
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", subject.ToString()),
            new("email", email),
            new("name", name),
            new("email_verified", emailVerified ? "true" : "false"),
        };
        claims.AddRange(roles.Select(role => new Claim("roles", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

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
        // The InMemory provider enforces neither the NOT NULL on
        // PatientAccounts.PatientId nor the FK to Patients, so this suite
        // cannot catch an insert structurally the way Postgres would — the
        // explicit zero-row assertion below is the only guard.
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.PatientAccounts.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientEmailNotVerified_ThrowsRatherThanBinding()
    {
        // Arrange
        await using var db = NewContext();
        var unclaimed = new PatientAccount
        {
            Id = Guid.NewGuid(),
            PatientId = Guid.NewGuid(),
            Email = "patient@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.PatientAccounts.Add(unclaimed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provisioner = new FirstLoginProvisioner(db);

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(Guid.NewGuid(), "patient@example.com", "A Patient", emailVerified: false),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        var row = await db.PatientAccounts.SingleAsync(TestContext.Current.CancellationToken);
        row.ExternalSubject.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientEmailVerified_BindsUnclaimedAccountCaseInsensitively()
    {
        // Arrange
        await using var db = NewContext();
        var unclaimed = new PatientAccount
        {
            Id = Guid.NewGuid(),
            PatientId = Guid.NewGuid(),
            Email = "Patient@Example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.PatientAccounts.Add(unclaimed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provisioner = new FirstLoginProvisioner(db);
        var subject = Guid.NewGuid();

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "patient@example.com", "A Patient", emailVerified: true),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        localId.Should().Be(unclaimed.Id);
        var row = await db.PatientAccounts.SingleAsync(TestContext.Current.CancellationToken);
        row.ExternalSubject.Should().Be(subject);
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientAccountDisabled_ThrowsRatherThanReturningId()
    {
        // Arrange
        await using var db = NewContext();
        var subject = Guid.NewGuid();
        var disabled = new PatientAccount
        {
            Id = Guid.NewGuid(),
            PatientId = Guid.NewGuid(),
            Email = "patient@example.com",
            ExternalSubject = subject,
            DisabledAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.PatientAccounts.Add(disabled);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provisioner = new FirstLoginProvisioner(db);

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(subject, "patient@example.com", "A Patient"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProvisionAsync_WhenStaffTokenCarriesRealmRole_CreatesRowWithThatRole()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);
        var subject = Guid.NewGuid();

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "ops"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Staff.SingleAsync(TestContext.Current.CancellationToken);
        row.Id.Should().Be(localId);
        row.Role.Should().Be("ops");
    }

    [Fact]
    public async Task ProvisionAsync_WhenStaffTokenCarriesNoRecognisedRole_ThrowsRatherThanDefaulting()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);
        var subject = Guid.NewGuid();

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.Staff.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task ProvisionAsync_WhenStaffAccountDisabled_ThrowsRatherThanReturningId()
    {
        // Arrange
        await using var db = NewContext();
        var subject = Guid.NewGuid();
        var disabled = new Staff
        {
            Id = Guid.NewGuid(),
            ExternalSubject = subject,
            Email = "staffer@example.com",
            Name = "A Staffer",
            Role = "support",
            DisabledAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Staff.Add(disabled);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provisioner = new FirstLoginProvisioner(db);

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "support"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
