using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-23-account-export.sql against the previous schema and the bootstrap
// script (design/specs/2026-10-23-account-export-design.md, section 3).
[Collection(SharedAppHostCollection.Name)]
public class AccountExportMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-10-23-account-export.sql";

    private const string InsertProviderSql = """
        INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name")
        VALUES (@provider, @provider::text || '@test.local', gen_random_uuid(), 'X')
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AccountExportMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using (var db = await old.OpenAsync(Ct))
        {
            await RunPreviousMigrationsAsync(db);
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, MigrationFile);
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
        freshShape.Should().Contain(["AccountExports.Status", "AccountExports.FailureReason", "AccountExports.SizeBytes", "AccountExports.ExpiredAt"]);
        freshShape.Should().Contain(s => s.StartsWith("AccountExports:AccountExports_Provider_active_key"));
    }

    [Fact]
    public async Task AccountExportMigration_WithLegacyRows_MapsCompletedToDoneAndTheRestToFailed()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        await RunPreviousMigrationsAsync(db);
        var provider = Guid.NewGuid();
        await ExecuteAsync(db, InsertProviderSql, ("provider", provider));
        await ExecuteAsync(db, """
            INSERT INTO "AccountExports" ("ProviderId","CompletedAt","ObjectKey") VALUES (@provider, now(), 'exports/a.zip');
            INSERT INTO "AccountExports" ("ProviderId") VALUES (@provider);
            INSERT INTO "AccountExports" ("ProviderId","ObjectKey") VALUES (@provider, 'exports/b.zip');
            """, ("provider", provider));

        // Act
        await RunMigrationAsync(db, MigrationFile);
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE "Status" = 'done' AND "ObjectKey" = 'exports/a.zip'),
                   count(*) FILTER (WHERE "Status" = 'failed' AND "ObjectKey" IS NULL),
                   count(*)
              FROM "AccountExports" WHERE "ProviderId" = @provider
            """, db);
        command.Parameters.AddWithValue("provider", provider);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        reader.GetInt64(1).Should().Be(2);
        reader.GetInt64(2).Should().Be(3);
    }

    [Fact]
    public async Task AccountExports_SecondQueuedOrRunningExportForTheProvider_IsRejected()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var provider = Guid.NewGuid();
        await ExecuteAsync(db, InsertProviderSql, ("provider", provider));
        await ExecuteAsync(db, """INSERT INTO "AccountExports" ("ProviderId","Status") VALUES (@provider, 'queued')""", ("provider", provider));

        // Act
        var act = () => ExecuteAsync(db, """INSERT INTO "AccountExports" ("ProviderId","Status") VALUES (@provider, 'running')""", ("provider", provider));

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task AccountExports_FinishedExportsAndOtherProviders_DoNotBlockANewOne()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var provider = Guid.NewGuid();
        var other = Guid.NewGuid();
        await ExecuteAsync(db, InsertProviderSql, ("provider", provider));
        await ExecuteAsync(db, InsertProviderSql, ("provider", other));
        await ExecuteAsync(db, """
            INSERT INTO "AccountExports" ("ProviderId","Status") VALUES (@provider, 'failed');
            INSERT INTO "AccountExports" ("ProviderId","Status","CompletedAt","ObjectKey") VALUES (@provider, 'done', now(), 'exports/a.zip');
            INSERT INTO "AccountExports" ("ProviderId","Status","CompletedAt") VALUES (@provider, 'expired', now());
            INSERT INTO "AccountExports" ("ProviderId","Status") VALUES (@other, 'running');
            """, ("provider", provider), ("other", other));

        // Act
        var act = () => ExecuteAsync(db, """INSERT INTO "AccountExports" ("ProviderId","Status") VALUES (@provider, 'queued')""", ("provider", provider));

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("'done'", "NULL", "NULL")]
    [InlineData("'queued'", "now()", "NULL")]
    [InlineData("'failed'", "NULL", "'exports/a.zip'")]
    [InlineData("'whatever'", "NULL", "NULL")]
    public async Task AccountExports_InconsistentStatusCompletionOrKey_IsRejected(string status, string completedAt, string objectKey)
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var provider = Guid.NewGuid();
        await ExecuteAsync(db, InsertProviderSql, ("provider", provider));

        // Act
        var act = () => ExecuteAsync(db, $"""INSERT INTO "AccountExports" ("ProviderId","Status","CompletedAt","ObjectKey") VALUES (@provider, {status}, {completedAt}, {objectKey})""", ("provider", provider));

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task AccountExports_UnknownProvider_IsRejected()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);

        // Act
        var act = () => ExecuteAsync(db, """INSERT INTO "AccountExports" ("ProviderId") VALUES (@provider)""", ("provider", Guid.NewGuid()));

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");
    }

    [Fact]
    public async Task AccountExportMigration_RunAfterTheExternalDocumentsMigration_KeepsTheWiderActionList()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);

        // Act
        var act = () => ExecuteAsync(db, """INSERT INTO "AuditEntries" ("ActorId","Action") VALUES (gen_random_uuid(), 'external_document_opened')""");

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("account_export_requested", true)]
    [InlineData("account_export_downloaded", true)]
    [InlineData("signed", false)]
    public async Task AuditEntries_WithoutNoteOrDocument_AllowsOnlyTheExportActions(string action, bool allowed)
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);

        // Act
        var act = () => ExecuteAsync(db, """INSERT INTO "AuditEntries" ("ActorId","Action") VALUES (gen_random_uuid(), @action)""", ("action", action));

        // Assert
        if (allowed)
        {
            await act.Should().NotThrowAsync();
        }
        else
        {
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        }
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

    private static async Task RunPreviousMigrationsAsync(NpgsqlConnection db)
    {
        await RunMigrationAsync(db, "2026-10-03-patient-accounts.sql");
        await RunMigrationAsync(db, "2026-10-04-patient-invitations.sql");
        await RunMigrationAsync(db, "2026-10-07-appointment-notifications.sql");
        await RunMigrationAsync(db, "2026-10-10-live-session.sql");
        await RunMigrationAsync(db, "2026-10-12-note-structuring.sql");
        await RunMigrationAsync(db, "2026-10-13-note-review.sql");
        await RunMigrationAsync(db, "2026-10-17-note-pdf.sql");
        await RunMigrationAsync(db, "2026-10-18-scales.sql");
        await RunMigrationAsync(db, "2026-10-19-documents.sql");
        await RunMigrationAsync(db, "2026-10-20-documents-team.sql");
        await RunMigrationAsync(db, "2026-10-21-theme-vocabulary.sql");
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
            SELECT tablename || ':' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = ANY (@tables)
            """, db);
        string[] tables = ["AccountExports", "AuditEntries"];
        command.Parameters.AddWithValue("tables", tables);
        command.Parameters.AddWithValue("quoted", tables.Select(t => $"\"{t}\"").ToArray());
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var shape = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            var line = reader.GetString(0);
            // Later migrations own the full action list of this constraint (see MigrationFile).
            if (!line.Contains("AuditEntries_subject_ck"))
            {
                shape.Add(line);
            }
        }
        return shape;
    }
}
