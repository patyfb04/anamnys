using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-18-scales.sql against the previous schema and the bootstrap
// script (design/specs/2026-10-18-psychometric-scales-design.md).
[Collection(SharedAppHostCollection.Name)]
public class ScalesMigrationTests(SharedAppHostFixture fixture)
{
    private const string InsertRemovedWithoutRemoverSql = """
        WITH p AS (INSERT INTO "Providers" ("Email","ExternalSubject","Name")
                   VALUES ('x@test.local', gen_random_uuid(), 'X') RETURNING "Id"),
             pa AS (INSERT INTO "Patients" ("ProviderId","FirstName","LastName")
                    SELECT "Id", 'A', 'B' FROM p RETURNING "Id")
        INSERT INTO "ScaleApplications" ("PatientId","InstrumentId","Score","AppliedAt","RemovedAt")
        SELECT pa."Id", (SELECT "Id" FROM "Instruments" WHERE "Code" = 'PHQ-9'), 5, now(), now() FROM pa
        """;

    private static readonly string[] ExpectedCodes =
        ["PHQ-9", "MOM-D", "MOM-A", "DASS-21", "HAD", "ROSENBERG", "EPDS", "MBI", "BSQ", "GAD-7", "PCL-5", "BDI-2", "BAI", "ORS", "SRS"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ScalesMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
    {
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using (var db = await old.OpenAsync(Ct))
        {
            await RunPreviousMigrationsAsync(db);
            await RunMigrationAsync(db, "2026-10-18-scales.sql");
            await RunMigrationAsync(db, "2026-10-18-scales.sql");
            await RunMigrationAsync(db, "2026-10-19-documents.sql"); // the bootstrap script already includes it
            await RunMigrationAsync(db, "2026-10-20-documents-team.sql"); // the bootstrap script already includes it
        }
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using (var db = await fresh.OpenAsync(Ct))
        {
            await RunMigrationAsync(db, "2026-10-18-scales.sql");
        }

        var oldShape = await ShapeAsync(old);
        var freshShape = await ShapeAsync(fresh);
        oldShape.Should().BeEquivalentTo(freshShape);
        freshShape.Should().Contain(["ScaleApplications.RemovedAt", "ScaleApplications.RemovedBy"]);
    }

    [Fact]
    public async Task Catalog_AfterMigrationTwice_Has15Instruments()
    {
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        await RunPreviousMigrationsAsync(db);

        await RunMigrationAsync(db, "2026-10-18-scales.sql");
        await RunMigrationAsync(db, "2026-10-18-scales.sql");

        await using var select = new NpgsqlCommand("""SELECT "Code", "Enabled" FROM "Instruments" """, db);
        await using var reader = await select.ExecuteReaderAsync(Ct);
        var rows = new List<(string Code, bool Enabled)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetBoolean(1)));
        }
        rows.Select(r => r.Code).Should().BeEquivalentTo(ExpectedCodes);
        rows.Count(r => r.Enabled).Should().Be(3);
    }

    [Fact]
    public async Task RemovedWithoutRemover_IsRejected()
    {
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, "2026-10-18-scales.sql");
        await using var command = new NpgsqlCommand(InsertRemovedWithoutRemoverSql, db);

        var act = () => command.ExecuteNonQueryAsync(Ct);

        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ScaleApplications_Removed_ck");
    }

    private static async Task RunPreviousMigrationsAsync(NpgsqlConnection db)
    {
        await RunMigrationAsync(db, "2026-10-03-patient-accounts.sql");
        await RunMigrationAsync(db, "2026-10-04-patient-invitations.sql");
        await RunMigrationAsync(db, "2026-10-07-appointment-notifications.sql");
        await RunMigrationAsync(db, "2026-10-10-live-session.sql");
        await RunMigrationAsync(db, "2026-10-12-note-structuring.sql");
        await RunMigrationAsync(db, "2026-10-13-note-review.sql");
        await RunMigrationAsync(db, "2026-10-17-note-pdf.sql");
    }

    private static async Task RunMigrationAsync(NpgsqlConnection db, string file)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", file), Ct);
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<string>> ShapeAsync(ScratchDatabase scratch)
    {
        await using var db = await scratch.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT table_name || '.' || column_name
              FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = ANY (@tables)
            UNION ALL
            SELECT c.conrelid::regclass::text || ':' || c.conname || ' ' || pg_get_constraintdef(c.oid)
              FROM pg_constraint c WHERE c.conrelid::regclass::text = ANY (@quoted)
            UNION ALL
            SELECT tablename || ':' || indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = ANY (@tables)
            """, db);
        string[] tables = ["ScaleApplications", "Instruments", "ProviderInstrumentOptIns"];
        command.Parameters.AddWithValue("tables", tables);
        command.Parameters.AddWithValue("quoted", tables.Select(t => $"\"{t}\"").ToArray());
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var shape = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            shape.Add(reader.GetString(0));
        }
        return shape;
    }
}
