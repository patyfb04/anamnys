using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-30-patient-quotes.sql against the previous schema and the bootstrap script
// (design/specs/2026-10-30-patient-quotes-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class PatientQuotesMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-10-30-patient-quotes.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
        "2026-10-27-medication-timeline.sql", "2026-10-28-retention-panel.sql", "2026-10-29-theme-absence.sql", "2026-10-30-plan-adherence.sql",
    ];

    [Fact]
    public async Task PatientQuotesMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
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
            // An earlier file re-run after this one keeps the action list (its guard sees its own action).
            await RunMigrationAsync(db, "2026-10-30-plan-adherence.sql");
            await RunMigrationAsync(db, "2026-10-31-record-search.sql"); // the bootstrap script already includes it
            await RunMigrationAsync(db, "2026-11-01-protected-sharing.sql"); // the bootstrap script already includes it
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
        freshShape.Should().Contain("PatientQuotes.AddedById uuid YES ");
        freshShape.Should().Contain("PatientQuotes.RemovedAt timestamp with time zone YES ");
        freshShape.Should().Contain("PatientQuotes.RemovedById uuid YES ");
        freshShape.Should().Contain(s => s.Contains("PatientQuotes_Removed_ck"));
        freshShape.Should().Contain(s => s.Contains("PatientQuotes_NoteId_idx"));
        freshShape.Should().Contain(s => s.Contains("AuditEntries_subject_ck") && s.Contains("quote_recorded") && s.Contains("quote_removed") && s.Contains("follow_up_reopened"));
    }

    [Fact]
    public async Task PatientQuotesMigration_KeepsExistingQuotes()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        foreach (var file in PreviousMigrations)
        {
            await RunMigrationAsync(db, file);
        }
        var provider = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var note = Guid.NewGuid();
        await ExecuteAsync(db, """
            INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'P');
            INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'Ana', 'Silva');
            INSERT INTO "Notes" ("Id","ProviderId","PatientId","Status","InputMode","Format") VALUES (@n, @p, @pa, 'ReadyForReview', 'Text', 'DAP');
            INSERT INTO "PatientQuotes" ("NoteId","Text","RecordedAt") VALUES (@n, 'fala', now());
            """, ("p", provider), ("pa", patient), ("n", note));

        // Act
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        (await ScalarAsync(db, """SELECT count(*) FROM "PatientQuotes" WHERE "NoteId" = @n AND "RemovedAt" IS NULL AND "AddedById" IS NULL""", ("n", note))).Should().Be(1L);
    }

    [Fact]
    public async Task AuditEntries_AcceptTheQuoteActionsWithoutANote_AndStillRefuseOthers()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);

        // Act
        foreach (var action in new[] { "quote_recorded", "quote_removed" })
        {
            await ExecuteAsync(db, """INSERT INTO "AuditEntries" ("Action") VALUES (@a)""", ("a", action));
        }
        var other = () => ExecuteAsync(db, """INSERT INTO "AuditEntries" ("Action") VALUES ('note_signed')""");

        // Assert
        (await ScalarAsync(db, """SELECT count(*) FROM "AuditEntries" """)).Should().Be(2L);
        (await other.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Theory]
    [InlineData("now()", "NULL")]
    [InlineData("NULL", "gen_random_uuid()")]
    public async Task PatientQuotes_RefuseOneRemovalColumnWithoutTheOther(string removedAt, string removedById)
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var provider = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var note = Guid.NewGuid();
        await ExecuteAsync(db, """
            INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'P');
            INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'Ana', 'Silva');
            INSERT INTO "Notes" ("Id","ProviderId","PatientId","Status","InputMode","Format") VALUES (@n, @p, @pa, 'ReadyForReview', 'Text', 'DAP');
            """, ("p", provider), ("pa", patient), ("n", note));

        // Act
        var insert = () => ExecuteAsync(db,
            $"""INSERT INTO "PatientQuotes" ("NoteId","Text","RecordedAt","RemovedAt","RemovedById") VALUES (@n, 'fala', now(), {removedAt}, {removedById})""", ("n", note));

        // Assert
        (await insert.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("PatientQuotes_Removed_ck");
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
             WHERE table_schema = 'public' AND table_name = 'PatientQuotes'
            UNION ALL
            SELECT c.conrelid::regclass::text || ':' || c.conname || ' ' || pg_get_constraintdef(c.oid)
              FROM pg_constraint c WHERE c.conrelid::regclass::text IN ('"PatientQuotes"', '"AuditEntries"')
            UNION ALL
            SELECT indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'PatientQuotes'
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
