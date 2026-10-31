using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-29-theme-absence.sql against the previous schema and the bootstrap script
// (design/specs/2026-10-29-theme-absence-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class ThemeAbsenceMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-10-29-theme-absence.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
        "2026-10-27-medication-timeline.sql", "2026-10-28-retention-panel.sql",
    ];

    [Fact]
    public async Task ThemeAbsenceMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using (var db = await old.OpenAsync(Ct))
        {
            foreach (var file in PreviousMigrations)
            {
                await RunMigrationAsync(db, file);
            }
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, "2026-10-30-plan-adherence.sql"); // the bootstrap script already includes it
        }
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using (var db = await fresh.OpenAsync(Ct))
        {
            await RunMigrationAsync(db, MigrationFile);
        }

        // Act
        var oldShape = await ShapeAsync(old);
        var freshShape = await ShapeAsync(fresh);

        // Assert
        oldShape.Should().BeEquivalentTo(freshShape);
        freshShape.Should().Contain("Providers.AbsenceSessions");
        freshShape.Should().Contain(s => s.Contains("Providers_AbsenceSessions_ck"));
    }

    [Fact]
    public async Task Providers_AbsenceSessions_DefaultsToFour_AndAcceptsOnlyTwoToTwenty()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var provider = Guid.NewGuid();
        await ExecuteAsync(db, """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'P')""", ("p", provider));

        // Act
        var days = await ScalarAsync(db, """SELECT "AbsenceSessions" FROM "Providers" WHERE "Id" = @p""", ("p", provider));
        await ExecuteAsync(db, """UPDATE "Providers" SET "AbsenceSessions" = 2 WHERE "Id" = @p""", ("p", provider));
        await ExecuteAsync(db, """UPDATE "Providers" SET "AbsenceSessions" = 20 WHERE "Id" = @p""", ("p", provider));
        var one = () => ExecuteAsync(db, """UPDATE "Providers" SET "AbsenceSessions" = 1 WHERE "Id" = @p""", ("p", provider));
        var twentyOne = () => ExecuteAsync(db, """UPDATE "Providers" SET "AbsenceSessions" = 21 WHERE "Id" = @p""", ("p", provider));

        // Assert
        days.Should().Be(4);
        (await one.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        (await twentyOne.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task ThemeAbsenceMigration_KeepsExistingProvidersAtTheDefault()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        foreach (var file in PreviousMigrations)
        {
            await RunMigrationAsync(db, file);
        }
        var provider = Guid.NewGuid();
        await ExecuteAsync(db, """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'P')""", ("p", provider));

        // Act
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        (await ScalarAsync(db, """SELECT "AbsenceSessions" FROM "Providers" WHERE "Id" = @p""", ("p", provider))).Should().Be(4);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteScalarAsync(Ct);
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
            SELECT table_name || '.' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(column_default, '')
              FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = 'Providers'
            UNION ALL
            SELECT table_name || '.' || column_name
              FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = 'Providers'
            UNION ALL
            SELECT c.conrelid::regclass::text || ':' || c.conname || ' ' || pg_get_constraintdef(c.oid)
              FROM pg_constraint c WHERE c.conrelid::regclass::text = '"Providers"'
            """, db);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var shape = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            shape.Add(reader.GetString(0));
        }
        return shape;
    }
}
