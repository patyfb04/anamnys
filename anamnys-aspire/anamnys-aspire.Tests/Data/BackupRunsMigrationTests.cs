using Anamnys.Server.Backup;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-11-05-backup-runs.sql against the bootstrap schema: tables, checks, the partial unique index,
// the agent role and its grants (design/specs/2026-11-05-backup-restore-design.md, section 3).
[Collection(SharedAppHostCollection.Name)]
public class BackupRunsMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-11-05-backup-runs.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_RunTwiceOnTheBootstrapSchema_Succeeds_AndCreatesTablesRoleAndIndex()
    {
        // Arrange
        await using var scratch = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await scratch.OpenAsync(Ct);

        // Act
        var canLoginBefore = await RoleCanLoginAsync(db);
        await RunMigrationAsync(db, MigrationFile);
        await RunMigrationAsync(db, MigrationFile);
        var canLoginAfter = await RoleCanLoginAsync(db);

        // Assert
        (await ScalarAsync(db, """SELECT count(*) FROM information_schema.tables WHERE table_name IN ('BackupRuns', 'BackupAlerts')""")).Should().Be(2);
        (await ScalarAsync(db, """SELECT count(*) FROM pg_indexes WHERE indexname = 'BackupRuns_Kind_FinishedAt_idx' AND indexdef LIKE '%"FinishedAt" DESC%'""")).Should().Be(1);
        (await ScalarAsync(db, """SELECT count(*) FROM pg_indexes WHERE indexname = 'BackupAlerts_OpenCheck_key' AND indexdef LIKE 'CREATE UNIQUE%' AND indexdef LIKE '%WHERE%ClosedAt%IS NULL%'""")).Should().Be(1);
        (await ScalarAsync(db, """SELECT count(*) FROM pg_roles WHERE rolname = 'anamnys_backup_agent' AND NOT rolsuper""")).Should().Be(1);
        // Roles are cluster-wide: the backup agent may have been granted LOGIN by an earlier run; the migration must not change it.
        canLoginAfter.Should().Be(canLoginBefore);
    }

    private static async Task<bool?> RoleCanLoginAsync(NpgsqlConnection db)
    {
        await using var command = new NpgsqlCommand("SELECT rolcanlogin FROM pg_roles WHERE rolname = 'anamnys_backup_agent'", db);
        return await command.ExecuteScalarAsync(Ct) as bool?;
    }

    [Fact]
    public async Task BackupRuns_RejectsAnUnknownKindOrOutcome()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);

        // Act
        var badKind = async () => await s.ExecuteAsync("""INSERT INTO "BackupRuns" ("Kind","StartedAt","FinishedAt","Outcome") VALUES ('weekly', now(), now(), 'ok')""", Ct);
        var badOutcome = async () => await s.ExecuteAsync("""INSERT INTO "BackupRuns" ("Kind","StartedAt","FinishedAt","Outcome") VALUES ('full', now(), now(), 'partial')""", Ct);
        await s.ExecuteAsync("""INSERT INTO "BackupRuns" ("Kind","StartedAt","FinishedAt","Outcome","Bytes","BackupName") VALUES ('retention', now(), now(), 'ok', 10, 'base_0001')""", Ct);

        // Assert
        await badKind.Should().ThrowAsync<PostgresException>().WithMessage("*BackupRuns_Kind_ck*");
        await badOutcome.Should().ThrowAsync<PostgresException>().WithMessage("*BackupRuns_Outcome_ck*");
        (await s.CountAsync("""SELECT count(*) FROM "BackupRuns" """, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task BackupAlerts_AllowsOnlyOneOpenAlertPerCheck()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await s.ExecuteAsync("""INSERT INTO "BackupAlerts" ("Check","OpenedAt") VALUES ('wal', now())""", Ct);

        // Act
        var second = async () => await s.ExecuteAsync("""INSERT INTO "BackupAlerts" ("Check","OpenedAt") VALUES ('wal', now())""", Ct);
        await s.ExecuteAsync("""INSERT INTO "BackupAlerts" ("Check","OpenedAt") VALUES ('full', now())""", Ct);
        await s.ExecuteAsync("""UPDATE "BackupAlerts" SET "ClosedAt" = now() WHERE "Check" = 'wal'""", Ct);
        await s.ExecuteAsync("""INSERT INTO "BackupAlerts" ("Check","OpenedAt") VALUES ('wal', now())""", Ct);

        // Assert
        await second.Should().ThrowAsync<PostgresException>().WithMessage("*BackupAlerts_OpenCheck_key*");
        (await s.CountAsync("""SELECT count(*) FROM "BackupAlerts" WHERE "Check" = 'wal' """, Ct)).Should().Be(2);
    }

    [Fact]
    public async Task AgentRole_CanOnlyInsertRunsAndReadTheSnapshotTables()
    {
        // Arrange
        await using var scratch = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await scratch.OpenAsync(Ct);
        await Execute(db, "SET ROLE anamnys_backup_agent");

        // Act
        await Execute(db, """INSERT INTO "BackupRuns" ("Kind","StartedAt","FinishedAt","Outcome") VALUES ('full', now(), now(), 'ok')""");
        var snapshotTables = new[]
        {
            "Providers", "Patients", "Notes", "NoteSections", "ClinicalDocuments", "ExternalDocuments",
            "IntegritySeals", "IntegrityAnchors", "AuditEntries", "Dossiers", "ShareLinks",
        };
        foreach (var table in snapshotTables)
        {
            await Execute(db, $"""SELECT count(*) FROM "{table}" """);
        }
        var readRuns = async () => await Execute(db, """SELECT count(*) FROM "BackupRuns" """);
        var updateRuns = async () => await Execute(db, """UPDATE "BackupRuns" SET "Outcome" = 'failed'""");
        var readAlerts = async () => await Execute(db, """SELECT count(*) FROM "BackupAlerts" """);
        var readNotes = async () => await Execute(db, """SELECT "Content" FROM "NoteSections" """);

        // Assert
        await readRuns.Should().ThrowAsync<PostgresException>().WithMessage("*permission denied*");
        await updateRuns.Should().ThrowAsync<PostgresException>().WithMessage("*permission denied*");
        await readAlerts.Should().ThrowAsync<PostgresException>().WithMessage("*permission denied*");
        // Whole-table SELECT is granted (the agent only counts); the point is it has no write access to clinical tables.
        await readNotes.Should().NotThrowAsync();
        var writeNotes = async () => await Execute(db, """DELETE FROM "NoteSections" """);
        await writeNotes.Should().ThrowAsync<PostgresException>().WithMessage("*permission denied*");
    }

    [Fact]
    public async Task PgStatArchiver_IsReadableByANonSuperuserRole_AndTheReaderMatchesTheView()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = await s.OpenAsync(Ct);
        await Execute(db, "SET ROLE anamnys_backup_agent");

        // Act
        var rows = await ScalarAsync(db, "SELECT count(*) FROM pg_stat_archiver");
        await Execute(db, "RESET ROLE");
        DateTimeOffset? expectedArchived;
        DateTimeOffset? expectedFailed;
        await using (var command = new NpgsqlCommand("SELECT last_archived_time, last_failed_time FROM pg_stat_archiver", db))
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).Should().BeTrue();
            expectedArchived = await reader.IsDBNullAsync(0, Ct) ? null : reader.GetFieldValue<DateTimeOffset>(0);
            expectedFailed = await reader.IsDBNullAsync(1, Ct) ? null : reader.GetFieldValue<DateTimeOffset>(1);
        }
        await using var context = s.CreateDbContext();
        var status = await new PgStatArchiverReader().ReadAsync(context, Ct);

        // Assert
        rows.Should().Be(1);
        status.LastArchivedAt.Should().Be(expectedArchived);
        status.LastFailedAt.Should().Be(expectedFailed);
    }

    private static async Task Execute(NpgsqlConnection db, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection db, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task RunMigrationAsync(NpgsqlConnection db, string file)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", file), Ct);
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
