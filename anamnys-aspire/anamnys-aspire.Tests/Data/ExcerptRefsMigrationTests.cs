using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-24-excerpt-refs.sql against the previous schema and the bootstrap script
// (design/specs/2026-10-24-excerpt-references-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class ExcerptRefsMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-10-24-excerpt-refs.sql";
    private const string InsertProviderSql = """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'P')""";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExcerptRefsMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using (var db = await old.OpenAsync(Ct))
        {
            await RunPreviousMigrationsAsync(db);
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, "2026-10-25-external-documents.sql"); // the bootstrap script already includes it
            await RunMigrationAsync(db, "2026-10-26-document-facts.sql"); // the bootstrap script already includes it
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
        freshShape.Should().Contain(["ExcerptRefs.ProviderId", "ExcerptRefs.SectionKey", "ExcerptRefs.Quote", "ExcerptRefs.Kind"]);
        freshShape.Should().Contain(s => s.StartsWith("ExcerptRefs:ExcerptRefs_Provider_Source_idx"));
    }

    [Fact]
    public async Task ExcerptRefsMigration_ActiveThemeTags_GetSectionReferencesToTheFirstNonEmptySection_Idempotently()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        await RunPreviousMigrationsAsync(db);
        var (provider, patient, session) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (withSession, withoutSession, empty, removed) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(db, InsertProviderSql, ("p", provider));
        await ExecuteAsync(db, """INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'A', 'B')""",
            ("pa", patient), ("p", provider));
        await ExecuteAsync(db, """INSERT INTO "Sessions" ("Id","PatientId","ProviderId","ScheduledStart") VALUES (@s, @pa, @p, '2026-10-01T10:00:00Z')""",
            ("s", session), ("pa", patient), ("p", provider));
        foreach (var (note, sessionId) in new[] { (withSession, (object)session), (withoutSession, DBNull.Value), (empty, DBNull.Value), (removed, DBNull.Value) })
        {
            await ExecuteAsync(db, """
                INSERT INTO "Notes" ("Id","PatientId","ProviderId","SessionId","Status","InputMode") VALUES (@n, @pa, @p, @s, 'Draft', 'Text')
                """, ("n", note), ("pa", patient), ("p", provider), ("s", sessionId));
        }
        await ExecuteAsync(db, """
            INSERT INTO "NoteSections" ("NoteId","Key","Content","Position") VALUES
              (@a, 'data', '  ', 0), (@a, 'subjective', 'texto A', 1),
              (@b, 'data', 'texto B', 0),
              (@c, 'data', '', 0),
              (@d, 'data', 'texto D', 0)
            """, ("a", withSession), ("b", withoutSession), ("c", empty), ("d", removed));
        var term = await TermIdAsync(db);
        foreach (var note in new[] { withSession, withoutSession, empty, removed })
        {
            await ExecuteAsync(db, """
                INSERT INTO "ThemeTags" ("NoteId","TermId","CreatedBy","RemovedAt","RemovedById")
                VALUES (@n, @t, 'ai', CASE WHEN @rm THEN now() END, CASE WHEN @rm THEN @p END)
                """, ("n", note), ("t", term), ("rm", note == removed), ("p", provider));
        }

        // Act
        await RunMigrationAsync(db, MigrationFile);
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        await using var command = new NpgsqlCommand("""
            SELECT t."NoteId", r."Kind", r."SectionKey", r."DatedAt", r."ProviderId", r."SourceType"
              FROM "ThemeTags" t LEFT JOIN "ExcerptRefs" r ON r."Id" = t."ExcerptRefId"
            """, db);
        var rows = new Dictionary<Guid, (string? Kind, string? Key, DateTimeOffset? At, Guid? Provider, string? Source)>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                rows[reader.GetGuid(0)] = (
                    reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5));
            }
        }
        rows[withSession].Should().Be(("section", "subjective", new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), provider, "note"));
        rows[withoutSession].Kind.Should().Be("section");
        rows[withoutSession].Key.Should().Be("data");
        rows[empty].Kind.Should().BeNull();
        rows[removed].Kind.Should().BeNull();
        await using var count = new NpgsqlCommand("""SELECT count(*) FROM "ExcerptRefs" """, db);
        ((long)(await count.ExecuteScalarAsync(Ct))!).Should().Be(2);
    }

    [Theory]
    [InlineData("'exact'", "NULL", "NULL", "NULL", "'data'")]
    [InlineData("'exact'", "1", "NULL", "'x'", "'data'")]
    [InlineData("'section'", "1", "2", "'x'", "'data'")]
    [InlineData("'section'", "NULL", "NULL", "NULL", "NULL")]
    [InlineData("'whatever'", "NULL", "NULL", "NULL", "'data'")]
    [InlineData("'exact'", "1", "2", "repeat('x', 201)", "'data'")]
    public async Task ExcerptRefs_InconsistentRows_AreRejected(string kind, string start, string end, string quote, string sectionKey)
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var provider = Guid.NewGuid();
        await ExecuteAsync(db, InsertProviderSql, ("p", provider));

        // Act
        var act = () => ExecuteAsync(db, $"""
            INSERT INTO "ExcerptRefs" ("ProviderId","SourceType","SourceId","SectionKey","Kind","OffsetStart","OffsetEnd","Quote","DatedAt")
            VALUES (@p, 'note', gen_random_uuid(), {sectionKey}, {kind}, {start}, {end}, {quote}, now())
            """, ("p", provider));

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task ExcerptRefs_ValidExactAndSectionRows_AreAccepted_AndUnknownProviderRejected()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        var provider = Guid.NewGuid();
        await ExecuteAsync(db, InsertProviderSql, ("p", provider));

        // Act
        var valid = () => ExecuteAsync(db, """
            INSERT INTO "ExcerptRefs" ("ProviderId","SourceType","SourceId","SectionKey","Kind","OffsetStart","OffsetEnd","Quote","DatedAt")
            VALUES (@p, 'note', gen_random_uuid(), 'data', 'exact', 0, 1, 'x', now()),
                   (@p, 'note', gen_random_uuid(), 'data', 'section', NULL, NULL, NULL, now())
            """, ("p", provider));
        var unknown = () => ExecuteAsync(db, """
            INSERT INTO "ExcerptRefs" ("ProviderId","SourceType","SourceId","SectionKey","Kind","DatedAt")
            VALUES (@p, 'note', gen_random_uuid(), 'data', 'section', now())
            """, ("p", Guid.NewGuid()));

        // Assert
        await valid.Should().NotThrowAsync();
        (await unknown.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");
    }

    private static async Task<Guid> TermIdAsync(NpgsqlConnection db)
    {
        await using var command = new NpgsqlCommand("""SELECT "Id" FROM "ThemeTerms" ORDER BY "Code" LIMIT 1""", db);
        return (Guid)(await command.ExecuteScalarAsync(Ct))!;
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
        await RunMigrationAsync(db, "2026-10-23-account-export.sql");
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
        string[] tables = ["ExcerptRefs"];
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
