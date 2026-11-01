using System.Text.RegularExpressions;
using Anamnys.Server.Analysis;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-31-record-search.sql against the previous schema and the bootstrap script
// (design/specs/2026-10-31-record-search-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class RecordSearchMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-10-31-record-search.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
        "2026-10-27-medication-timeline.sql", "2026-10-28-retention-panel.sql", "2026-10-29-theme-absence.sql", "2026-10-30-plan-adherence.sql",
        "2026-10-30-patient-quotes.sql",
    ];

    [Fact]
    public async Task RecordSearchMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
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
        freshShape.Should().Contain(s => s.StartsWith("extension unaccent", StringComparison.Ordinal));
        freshShape.Should().Contain(s => s.StartsWith("config anamnys_pt", StringComparison.Ordinal));
        freshShape.Should().Contain(s => s.Contains("NoteSections_Content_fts_idx") && s.Contains("gin") && s.Contains("anamnys_pt"));
    }

    [Theory]
    [InlineData("A relação com a mãe piorou.", "relacao")]
    [InlineData("A relacao com a mae piorou.", "relação")]
    [InlineData("Foi a dois médicos.", "medico")]
    public async Task AnamnysPt_MatchesAcrossAccentsAndInflection(string text, string word)
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);

        // Act
        await using var command = new NpgsqlCommand("SELECT to_tsvector('anamnys_pt', @t) @@ plainto_tsquery('anamnys_pt', @w)", db);
        command.Parameters.AddWithValue("t", text);
        command.Parameters.AddWithValue("w", word);
        var matches = await command.ExecuteScalarAsync(Ct);

        // Assert
        matches.Should().Be(true);
    }

    [Fact]
    public async Task TheIndex_IsUsableByTheSqlTheSearchGenerates()
    {
        // Arrange: without the NoteId indexes, which win for a single patient, the plan shows whether the expression EF
        // writes is the one of the GIN index (a bound configuration would not be).
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await using (var setup = new NpgsqlCommand("""
            DROP INDEX "NoteSections_NoteId_idx";
            ALTER TABLE "NoteSections" DROP CONSTRAINT "NoteSections_NoteId_Key_key";
            SET enable_seqscan = off;
            """, db))
        {
            await setup.ExecuteNonQueryAsync(Ct);
        }
        await using var context = fresh.CreateDbContext();
        List<Guid> noteIds = [Guid.NewGuid()];
        var generated = RecordSearchQuery.MatchingSections(context, noteIds, "'sono'").ToQueryString();
        var sql = string.Join('\n', generated.Split('\n').Where(line => !line.StartsWith("--", StringComparison.Ordinal)));
        sql.Should().Contain("to_tsvector('anamnys_pt'");

        // Act
        await using var command = new NpgsqlCommand("EXPLAIN " + sql, db);
        foreach (var name in Regex.Matches(sql, @"@(\w+)").Select(m => m.Groups[1].Value).Distinct())
        {
            command.Parameters.AddWithValue(name, name.Contains("note", StringComparison.OrdinalIgnoreCase) ? noteIds.ToArray() : "'sono'");
        }
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var plan = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            plan.Add(reader.GetString(0));
        }

        // Assert
        plan.Should().Contain(line => line.Contains("NoteSections_Content_fts_idx"));
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
            SELECT 'extension ' || extname FROM pg_extension WHERE extname = 'unaccent'
            UNION ALL
            SELECT 'config ' || c.cfgname || ' ' || t.alias || ' ' || m.mapseqno || ' ' || d.dictname
              FROM pg_ts_config c
              JOIN pg_ts_config_map m ON m.mapcfg = c.oid
              JOIN ts_token_type('default') t ON t.tokid = m.maptokentype
              JOIN pg_ts_dict d ON d.oid = m.mapdict
             WHERE c.cfgname = 'anamnys_pt'
            UNION ALL
            SELECT indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'NoteSections'
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
