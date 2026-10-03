using System.Security.Claims;
using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
        string? crpNumber = null,
        string? crpRegion = null,
        string? givenName = null,
        string? familyName = null,
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("sub", subject.ToString()),
            new("email", email),
            new("name", name),
            new("email_verified", emailVerified ? "true" : "false"),
        };
        if (crpNumber is not null)
        {
            claims.Add(new Claim("crpNumber", crpNumber));
        }
        if (crpRegion is not null)
        {
            claims.Add(new Claim("crpRegion", crpRegion));
        }
        if (givenName is not null)
        {
            claims.Add(new Claim("given_name", givenName));
        }
        if (familyName is not null)
        {
            claims.Add(new Claim("family_name", familyName));
        }
        claims.AddRange(roles.Select(role => new Claim("roles", role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task ProvisionAsync_WhenProviderIsUnknown_CreatesRowWithExternalSubject()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
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
    public async Task ProvisionAsync_WhenProviderRegisters_PersistsCrpNumberAndRegionFromClaims()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "clinician@example.com", "A Clinician", crpNumber: "123456", crpRegion: "06"),
            Realms.Providers,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Providers.SingleAsync(TestContext.Current.CancellationToken);
        row.CrpNumber.Should().Be("123456");
        row.CrpRegion.Should().Be("06");
    }

    [Fact]
    public async Task ProvisionAsync_WhenProviderTokenCarriesNoCrpClaims_LeavesBothNull()
    {
        // Arrange — a provider that authenticated before self-registration shipped
        // (or logged in via a token that simply carries neither claim) must not throw;
        // the Providers_Crp_ck constraint accepts both-null.
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "clinician@example.com", "A Clinician"),
            Realms.Providers,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Providers.SingleAsync(TestContext.Current.CancellationToken);
        row.CrpNumber.Should().BeNull();
        row.CrpRegion.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_WhenCalledTwiceForSameSubject_DoesNotCreateSecondRow()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
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
    public async Task ProvisionAsync_WhenPatientLogsInFirstTime_CreatesAccountAndNoRecord()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "newpatient@example.com", "A Patient", givenName: "A", familyName: "Patient"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert — a portal login is an account only; records belong to providers
        // (design/specs/2026-10-02-patient-accounts-design.md).
        var account = await db.PatientAccounts.SingleAsync(TestContext.Current.CancellationToken);
        account.Id.Should().Be(localId);
        account.ExternalSubject.Should().Be(subject);
        account.Email.Should().Be("newpatient@example.com");
        account.FirstName.Should().Be("A");
        account.LastName.Should().Be("Patient");
        (await db.Patients.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientTokenCarriesNoNameClaims_LeavesNamesEmpty()
    {
        // Arrange — mirrors ProvisionAsync_WhenProviderTokenCarriesNoCrpClaims_LeavesBothNull:
        // a missing claim must not throw; it is a realm-config gap to notice later, not a
        // reason to fail the login.
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(Guid.NewGuid(), "newpatient@example.com", "A Patient"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        var account = await db.PatientAccounts.SingleAsync(TestContext.Current.CancellationToken);
        account.FirstName.Should().Be("");
        account.LastName.Should().Be("");
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientLogsInTwiceForSameSubject_DoesNotCreateSecondAccount()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var principal = PrincipalFor(Guid.NewGuid(), "newpatient@example.com", "A Patient", givenName: "A", familyName: "Patient");

        // Act
        var first = await provisioner.ProvisionAsync(principal, Realms.Patients, TestContext.Current.CancellationToken);
        var second = await provisioner.ProvisionAsync(principal, Realms.Patients, TestContext.Current.CancellationToken);

        // Assert
        second.Should().Be(first);
        (await db.PatientAccounts.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientEmailNotVerified_ThrowsRatherThanCreating()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(Guid.NewGuid(), "nobody@example.com", "Nobody", emailVerified: false),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await db.PatientAccounts.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task ProvisionAsync_WhenAProviderRecordHasTheSameContactEmail_LeavesItUnlinked()
    {
        // Arrange — a provider typed this email in for scheduling notices. That is not an
        // invitation: a portal sign-up with the same verified email must not be linked to
        // the provider's record (design/specs/2026-10-01-patient-records-design.md §8).
        await using var db = NewContext();
        var providerRecord = new Patient
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            FirstName = "Ana",
            LastName = "Silva",
            ContactEmail = "Patient@Example.com",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Patients.Add(providerRecord);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(Guid.NewGuid(), "patient@example.com", "A Patient", emailVerified: true),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        var record = await db.Patients.SingleAsync(TestContext.Current.CancellationToken);
        record.AccountId.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientAccountDisabled_ThrowsRatherThanReturningId()
    {
        // Arrange
        await using var db = NewContext();
        var subject = Guid.NewGuid();
        db.PatientAccounts.Add(NewAccount(subject, "patient@example.com", disabled: true));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(subject, "patient@example.com", "A Patient"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientVerifiedEmailChanged_SyncsAccountEmail()
    {
        // Arrange
        await using var db = NewContext();
        var subject = Guid.NewGuid();
        var account = NewAccount(subject, "old@example.com");
        db.PatientAccounts.Add(account);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "new@example.com", "A Patient"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        localId.Should().Be(account.Id);
        (await db.PatientAccounts.SingleAsync(TestContext.Current.CancellationToken)).Email.Should().Be("new@example.com");
    }

    [Fact]
    public async Task ProvisionAsync_WhenNewPatientEmailBelongsToAnotherAccount_KeepsTheOldOne()
    {
        // Arrange
        await using var db = NewContext();
        var subject = Guid.NewGuid();
        var account = NewAccount(subject, "old@example.com");
        db.PatientAccounts.Add(account);
        db.PatientAccounts.Add(NewAccount(Guid.NewGuid(), "Taken@Example.com"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "taken@example.com", "A Patient"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert — the login still succeeds; only the sync is skipped.
        localId.Should().Be(account.Id);
        (await db.PatientAccounts.SingleAsync(a => a.Id == account.Id, TestContext.Current.CancellationToken))
            .Email.Should().Be("old@example.com");
    }

    private static PatientAccount NewAccount(Guid subject, string email, bool disabled = false) => new()
    {
        Id = Guid.NewGuid(),
        ExternalSubject = subject,
        Email = email,
        FirstName = "A",
        LastName = "Patient",
        DisabledAt = disabled ? DateTimeOffset.UtcNow : null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ProvisionAsync_WhenStaffTokenCarriesRealmRole_CreatesRowWithThatRole()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
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
    public async Task ProvisionAsync_WhenStaffTokenCarriesNoRecognisedRole_CreatesPendingRow()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "default-roles-anamnys-owners"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Staff.SingleAsync(TestContext.Current.CancellationToken);
        row.Id.Should().Be(localId);
        row.Role.Should().BeNull("realm membership alone confers no staff role; the row waits for approval");
    }

    [Fact]
    public async Task ProvisionAsync_WhenPendingStaffReturnsWithRole_ActivatesRow()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        var pendingId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "support"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert
        localId.Should().Be(pendingId);
        var row = await db.Staff.SingleAsync(TestContext.Current.CancellationToken);
        row.Role.Should().Be("support");
    }

    [Fact]
    public async Task ProvisionAsync_WhenStaffTokenRoleChanges_UpdatesStoredRole()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "support"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "ops"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Staff.SingleAsync(TestContext.Current.CancellationToken);
        row.Role.Should().Be("ops");
    }

    [Fact]
    public async Task ProvisionAsync_WhenActiveStaffTokenLosesRole_KeepsStoredRoleAndStillLogsIn()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        var activeId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "owner"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert — authorization reads the token, so the stale row grants nothing.
        localId.Should().Be(activeId);
        var row = await db.Staff.SingleAsync(TestContext.Current.CancellationToken);
        row.Role.Should().Be("owner");
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
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(subject, "staffer@example.com", "A Staffer", roles: "support"),
            Realms.Owners,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProvisionAsync_WhenReturningProviderHasNewVerifiedEmail_UpdatesStoredEmail()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "old@example.com", "A Clinician"), Realms.Providers, TestContext.Current.CancellationToken);

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "new@example.com", "A Clinician"), Realms.Providers, TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Providers.SingleAsync(TestContext.Current.CancellationToken);
        row.Email.Should().Be("new@example.com");
    }

    [Fact]
    public async Task ProvisionAsync_WhenReturningProviderEmailIsUnverified_KeepsStoredEmail()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "old@example.com", "A Clinician"), Realms.Providers, TestContext.Current.CancellationToken);

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "new@example.com", "A Clinician", emailVerified: false),
            Realms.Providers,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Providers.SingleAsync(TestContext.Current.CancellationToken);
        row.Email.Should().Be("old@example.com");
    }
}
