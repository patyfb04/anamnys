using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// Runs design/migrations/2026-10-03-patient-accounts.sql against a throwaway database
// loaded with the schema as it was before the split (Fixtures/schema-2026-10-02.sql), so
// the shared dev database is never touched. See
// design/specs/2026-10-02-patient-accounts-design.md §3.
[Collection(SharedAppHostCollection.Name)]
public class PatientAccountsMigrationTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_MovesLoginsToAccounts_AndIsIdempotent()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await scratch.OpenAsync(Ct);

        // Arrange — the three shapes the old single table could hold.
        var provider = await InsertProviderAsync(db);
        var selfRegistered = Guid.NewGuid();
        var selfSubject = Guid.NewGuid();
        var linked = Guid.NewGuid();
        var linkedSubject = Guid.NewGuid();
        var unlinked = Guid.NewGuid();
        await ExecuteAsync(db, """
            INSERT INTO "Patients" ("Id", "ProviderId", "FirstName", "LastName", "Email", "ExternalSubject", "Phone", "DateOfBirth", "LastLoginAt")
            VALUES (@self, NULL, 'Sol', 'Portal', 'sol@portal.test', @selfSub, '+55 11 90000-0000', '1990-01-02', now()),
                   (@linked, @provider, 'Lia', 'Ligada', 'lia@portal.test', @linkedSub, NULL, NULL, NULL),
                   (@unlinked, @provider, 'Ugo', 'Solto', NULL, NULL, NULL, NULL, NULL)
            """,
            ("self", selfRegistered), ("selfSub", selfSubject), ("linked", linked), ("linkedSub", linkedSubject),
            ("unlinked", unlinked), ("provider", provider));

        // Act
        await RunMigrationAsync(db);
        await RunMigrationAsync(db);

        // Assert
        var accounts = await QueryAsync(db, """SELECT "Id", "ExternalSubject", "Email", "FirstName", "Phone" FROM "PatientAccounts" ORDER BY "Email" """);
        accounts.Should().HaveCount(2);
        accounts[0].Should().Equal(linked, linkedSubject, "lia@portal.test", "Lia", DBNull.Value);
        accounts[1].Should().Equal(selfRegistered, selfSubject, "sol@portal.test", "Sol", "+55 11 90000-0000");

        var records = await QueryAsync(db, """SELECT "Id", "AccountId" FROM "Patients" ORDER BY "FirstName" """);
        records.Select(r => r[0]).Should().Equal(linked, unlinked);
        records[0][1].Should().Be(linked);
        records[1][1].Should().Be(DBNull.Value);

        (await ScalarAsync<long>(db, """SELECT count(*) FROM information_schema.columns WHERE table_name = 'Patients' AND column_name IN ('Email', 'ExternalSubject', 'LastLoginAt', 'TermsAcceptedAt', 'DisabledAt')"""))
            .Should().Be(0);
        (await ScalarAsync<string>(db, """SELECT is_nullable FROM information_schema.columns WHERE table_name = 'Patients' AND column_name = 'ProviderId'"""))
            .Should().Be("NO");
    }

    [Fact]
    public async Task Migration_SelfRegistrationWithClinicalRow_AbortsAndChangesNothing()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await scratch.OpenAsync(Ct);

        // Arrange
        var selfRegistered = Guid.NewGuid();
        await ExecuteAsync(db, """
            INSERT INTO "Patients" ("Id", "FirstName", "LastName", "Email", "ExternalSubject")
            VALUES (@id, 'Sol', 'Portal', 'sol@portal.test', @sub);
            INSERT INTO "MedicationEntries" ("PatientId", "Drug", "StartedOn") VALUES (@id, 'Sertralina', '2026-01-01');
            """, ("id", selfRegistered), ("sub", Guid.NewGuid()));

        // Act
        var act = () => RunMigrationAsync(db);

        // Assert — on a fresh connection: the failed one is left inside the aborted
        // transaction, exactly as psql's would be before it disconnects and rolls back.
        await act.Should().ThrowAsync<PostgresException>();
        await using var after = await scratch.OpenAsync(Ct);
        (await ScalarAsync<bool>(after, """SELECT to_regclass('public."PatientAccounts"') IS NOT NULL""")).Should().BeFalse();
        (await ScalarAsync<long>(after, """SELECT count(*) FROM information_schema.columns WHERE table_name = 'Patients' AND column_name = 'Email'"""))
            .Should().Be(1);
        (await ScalarAsync<long>(after, """SELECT count(*) FROM "Patients" """)).Should().Be(1);
    }

    [Fact]
    public async Task Migration_OnTheCurrentBootstrapSchema_IsANoOp()
    {
        // The bootstrap script and the migration must agree on the end state.
        await using var scratch = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await scratch.OpenAsync(Ct);
        var provider = await InsertProviderAsync(db);
        var record = Guid.NewGuid();
        await ExecuteAsync(db, """INSERT INTO "Patients" ("Id", "ProviderId", "FirstName", "LastName") VALUES (@id, @provider, 'Ana', 'Silva')""",
            ("id", record), ("provider", provider));

        // Act
        await RunMigrationAsync(db);

        // Assert
        (await QueryAsync(db, """SELECT "Id" FROM "Patients" """)).Select(r => r[0]).Should().Equal(record);
        (await ScalarAsync<long>(db, """SELECT count(*) FROM pg_constraint WHERE conname IN ('Patients_AccountId_fkey', 'Patients_Account_Provider_key')"""))
            .Should().Be(2);
    }

    [Fact]
    public async Task InvitationsMigration_AfterAccounts_AndOnTheCurrentBootstrapSchema_Agree()
    {
        // From the previous schema: accounts migration, then invitations migration (twice).
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using (var db = await old.OpenAsync(Ct))
        {
            await RunMigrationAsync(db);
            await RunMigrationAsync(db, "2026-10-04-patient-invitations.sql");
            await RunMigrationAsync(db, "2026-10-04-patient-invitations.sql");
            (await InvitationObjectsAsync(db)).Should().Be(5);
        }

        // On the current bootstrap schema the invitations migration changes nothing.
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using (var db = await fresh.OpenAsync(Ct))
        {
            (await InvitationObjectsAsync(db)).Should().Be(5);
            await RunMigrationAsync(db, "2026-10-04-patient-invitations.sql");
            (await InvitationObjectsAsync(db)).Should().Be(5);
        }
    }

    // The table's partial unique index, its two checks and its two foreign keys.
    private static async Task<long> InvitationObjectsAsync(NpgsqlConnection db) =>
        await ScalarAsync<long>(db, """
            SELECT (SELECT count(*) FROM pg_indexes WHERE indexname = 'PatientInvitations_OnePending_key')
                 + (SELECT count(*) FROM pg_constraint WHERE conname IN (
                       'PatientInvitations_Closed_ck', 'PatientInvitations_Accepted_ck',
                       'PatientInvitations_PatientId_fkey', 'PatientInvitations_AcceptedAccountId_fkey'))
            """);

    private static async Task RunMigrationAsync(NpgsqlConnection db, string file = "2026-10-03-patient-accounts.sql")
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", file), Ct);
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<Guid> InsertProviderAsync(NpgsqlConnection db)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(db, """INSERT INTO "Providers" ("Id", "Email", "ExternalSubject", "Name") VALUES (@id, @email, @sub, 'P')""",
            ("id", id), ("email", $"{id:N}@test.local"), ("sub", Guid.NewGuid()));
        return id;
    }

    private static async Task ExecuteAsync(NpgsqlConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection db, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<List<object[]>> QueryAsync(NpgsqlConnection db, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<object[]>();
        while (await reader.ReadAsync(Ct))
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    // A database created for one test from the pre-split bootstrap script and dropped after.
    private sealed class ScratchDatabase(string adminConnectionString, string name) : IAsyncDisposable
    {
        public static async Task<ScratchDatabase> CreateAsync(SharedAppHostFixture fixture, CancellationToken ct, string schemaFile = "Data/Fixtures/schema-2026-10-02.sql")
        {
            var admin = await fixture.GetConnectionStringAsync("anamnysdb", ct)
                ?? throw new InvalidOperationException("No connection string for anamnysdb.");
            var name = $"mig_{Guid.NewGuid():N}";
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync(ct);
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
                await create.ExecuteNonQueryAsync(ct);
            }

            var scratch = new ScratchDatabase(admin, name);
            await using var db = await scratch.OpenAsync(ct);
            var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, schemaFile), ct);
            await using var load = new NpgsqlCommand(schema, db);
            await load.ExecuteNonQueryAsync(ct);
            return scratch;
        }

        public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
        {
            var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name, Pooling = false }.ConnectionString);
            await connection.OpenAsync(ct);
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
