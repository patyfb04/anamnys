using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Tests.Data;

// The account/record split's guarantees live in the schema
// (design/specs/2026-10-02-patient-accounts-design.md §3), so they are tested against the
// real Postgres, not the EF model.
[Collection(SharedAppHostCollection.Name)]
public class PatientAccountConstraintTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeletingAnAccount_KeepsTheRecordUnlinked()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var record = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var account = await seed.BindPortalAccountAsync(record, Ct);

        // Act
        await seed.ExecuteSqlAsync("""DELETE FROM "PatientAccounts" WHERE "Id" = @id""", Ct, ("id", account));

        // Assert
        await using var db = seed.CreateDbContext();
        var row = await db.Patients.SingleAsync(p => p.Id == record, Ct);
        row.AccountId.Should().BeNull();
    }

    [Fact]
    public async Task AnAccount_HasAtMostOneRecordPerProvider()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var account = await seed.AddAccountAsync(Ct);
        var first = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var sameProvider = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var otherProvider = await seed.AddProviderAsync(Ct);
        var elsewhere = await seed.AddPatientAsync("Ana", "Silva", providerId: otherProvider, cancellationToken: Ct);
        await seed.LinkAccountAsync(first, account, Ct);

        // Act
        var duplicate = () => seed.LinkAccountAsync(sameProvider, account, Ct);
        await seed.LinkAccountAsync(elsewhere, account, Ct);

        // Assert
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("Patients_Account_Provider_key");
    }

    [Fact]
    public async Task AccountEmails_AreUniqueIgnoringCase()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var email = $"{Guid.NewGuid():N}@portal.test";
        await seed.AddAccountAsync(Ct, email);

        // Act
        var act = () => seed.AddAccountAsync(Ct, email.ToUpperInvariant());

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("PatientAccounts_Email_key");
    }

    [Fact]
    public async Task ARecord_RequiresAProvider()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);

        // Act
        var act = () => seed.ExecuteSqlAsync(
            """INSERT INTO "Patients" ("FirstName", "LastName") VALUES ('Ana', 'Silva')""", Ct);

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.NotNullViolation);
    }
}
