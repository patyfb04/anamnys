using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-21-theme-vocabulary.sql against the previous schema and the bootstrap
// script (design/specs/2026-10-21-theme-vocabulary-design.md, sections 2 and 3).
[Collection(SharedAppHostCollection.Name)]
public class ThemeVocabularyMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-10-21-theme-vocabulary.sql";

    private const string InsertNoteSql = """
        WITH p AS (INSERT INTO "Providers" ("Email","ExternalSubject","Name")
                   VALUES ('x@test.local', gen_random_uuid(), 'X') RETURNING "Id"),
             pa AS (INSERT INTO "Patients" ("ProviderId","FirstName","LastName")
                    SELECT "Id", 'A', 'B' FROM p RETURNING "Id")
        INSERT INTO "Notes" ("PatientId","ProviderId","Status","InputMode")
        SELECT pa."Id", (SELECT "Id" FROM p), 'Draft', 'Text' FROM pa
        RETURNING "Id"
        """;

    private const string InsertActiveTagSql = """
        INSERT INTO "ThemeTags" ("NoteId","TermId")
        VALUES (@note, (SELECT "Id" FROM "ThemeTerms" WHERE "Code" = 'sono'))
        """;

    private const string InsertRemovedWithoutRemoverSql = """
        INSERT INTO "ThemeTags" ("NoteId","TermId","RemovedAt")
        VALUES (@note, (SELECT "Id" FROM "ThemeTerms" WHERE "Code" = 'sono'), now())
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThemeVocabularyMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
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
        freshShape.Should().Contain(["ThemeTags.AddedById", "ThemeTags.RemovedById", "ThemeTermSuccessions.Kind", "ThemeTermChanges.PreviousLabel"]);
    }

    [Fact]
    public async Task Seed_AfterMigrationTwice_HasTwentyEightTopLevelAndTwelveChildrenInActiveMh1()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        await RunPreviousMigrationsAsync(db);

        // Act
        await RunMigrationAsync(db, MigrationFile);
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE t."ParentTermId" IS NULL), count(*) FILTER (WHERE t."ParentTermId" IS NOT NULL),
                   (SELECT count(*) FROM "ThemeVocabularies"), bool_and(v."Active")
              FROM "ThemeTerms" t JOIN "ThemeVocabularies" v ON v."Id" = t."VocabularyId" WHERE v."Version" = 'mh-1'
            """, db);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        reader.GetInt64(0).Should().Be(28);
        reader.GetInt64(1).Should().Be(12);
        reader.GetInt64(2).Should().Be(1);
        reader.GetBoolean(3).Should().BeTrue();
    }

    [Fact]
    public async Task ThemeTags_SecondActiveTagForSameNoteAndTerm_IsRejected()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var noteId = await InsertNoteAsync(db);
        await ExecuteAsync(db, InsertActiveTagSql, noteId);

        // Act
        var act = () => ExecuteAsync(db, InsertActiveTagSql, noteId);

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task ThemeVocabularies_SecondActiveVocabulary_IsRejected()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);

        // Act
        var act = () => ExecuteSqlAsync(db, """INSERT INTO "ThemeVocabularies" ("Version","Active") VALUES ('second', true)""");

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task ThemeTags_RemovedTagWithoutRemovedById_IsRejected()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var noteId = await InsertNoteAsync(db);

        // Act
        var act = () => ExecuteAsync(db, InsertRemovedWithoutRemoverSql, noteId);

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task ThemeVocabularyMigration_WithLegacyTags_BackfillsRemoverAndRemovesDuplicateActiveTags()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        await RunPreviousMigrationsAsync(db);
        await ExecuteSqlAsync(db, """INSERT INTO "ThemeVocabularies" ("Id","Version") VALUES (gen_random_uuid(), 'legacy')""");
        await ExecuteSqlAsync(db, """INSERT INTO "ThemeTerms" ("VocabularyId","Code","Label") SELECT "Id", 'legado', 'Legado' FROM "ThemeVocabularies" WHERE "Version" = 'legacy'""");
        var noteId = await InsertNoteAsync(db);
        await ExecuteSqlAsync(db, """
            INSERT INTO "ThemeTags" ("NoteId","TermId","CreatedBy","CreatedAt","RemovedAt")
            SELECT @note, "Id", 'ai', now() - interval '3 hours', now() FROM "ThemeTerms" WHERE "Code" = 'legado';
            INSERT INTO "ThemeTags" ("NoteId","TermId","CreatedBy","CreatedAt")
            SELECT @note, "Id", 'ai', now() - interval '2 hours' FROM "ThemeTerms" WHERE "Code" = 'legado';
            INSERT INTO "ThemeTags" ("NoteId","TermId","CreatedBy","CreatedAt")
            SELECT @note, "Id", 'human', now() - interval '1 hour' FROM "ThemeTerms" WHERE "Code" = 'legado';
            """, noteId);

        // Act
        await RunMigrationAsync(db, MigrationFile);
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE "RemovedAt" IS NULL),
                   count(*) FILTER (WHERE "RemovedAt" IS NOT NULL AND "RemovedById" = (SELECT "ProviderId" FROM "Notes" WHERE "Id" = @note)),
                   count(*) FILTER (WHERE "RemovedAt" IS NULL AND "CreatedBy" = 'ai'),
                   count(*)
              FROM "ThemeTags" WHERE "NoteId" = @note
            """, db);
        command.Parameters.AddWithValue("note", noteId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();
        reader.GetInt64(0).Should().Be(1, "only the earliest active tag survives");
        reader.GetInt64(1).Should().Be(2, "the soft-removed tag and the duplicate both name the note's provider as remover");
        reader.GetInt64(2).Should().Be(1, "the earliest active tag is the AI one");
        reader.GetInt64(3).Should().Be(3);
    }

    private static async Task ExecuteSqlAsync(NpgsqlConnection db, string sql, Guid? noteId = null)
    {
        await using var command = new NpgsqlCommand(sql, db);
        if (noteId is { } note)
        {
            command.Parameters.AddWithValue("note", note);
        }
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<Guid> InsertNoteAsync(NpgsqlConnection db)
    {
        await using var command = new NpgsqlCommand(InsertNoteSql, db);
        return (Guid)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection db, string sql, Guid noteId)
    {
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddWithValue("note", noteId);
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
        string[] tables = ["ThemeVocabularies", "ThemeTerms", "ThemeTags", "ThemeTermSuccessions", "ThemeTermChanges"];
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
