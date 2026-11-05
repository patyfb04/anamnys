using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-11-01-protected-sharing.sql against the previous schema and the bootstrap script
// (design/specs/2026-11-01-protected-sharing-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class ProtectedSharingMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-11-01-protected-sharing.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
        "2026-10-27-medication-timeline.sql", "2026-10-28-retention-panel.sql", "2026-10-29-theme-absence.sql", "2026-10-30-plan-adherence.sql",
        "2026-10-30-patient-quotes.sql", "2026-10-31-record-search.sql",
    ];

    [Fact]
    public async Task ProtectedSharingMigration_FromPreviousSchema_RunTwice_AgreesWithTheBootstrapSchema()
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
            await RunMigrationAsync(db, "2026-11-03-integrity-chain.sql"); // the bootstrap script already includes it
        }
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using (var db = await fresh.OpenAsync(Ct))
        {
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, "2026-11-03-integrity-chain.sql");
        }

        // Act
        var oldShape = await ShapeAsync(old);
        var freshShape = await ShapeAsync(fresh);

        // Assert
        oldShape.Should().BeEquivalentTo(freshShape);
        freshShape.Should().Contain(s => s.Contains("ProviderId") && s.Contains("NO"));
        freshShape.Should().Contain(s => s.Contains("DisclosureAuthorizations_Recipient_key") && s.Contains("UNIQUE"));
        freshShape.Should().Contain(s => s.Contains("ShareAccesses_Outcome_ck") && s.Contains("blocked"));
        freshShape.Should().Contain(s => s.Contains("AuditEntries_subject_ck") && s.Contains("disclosure_term_opened") && s.Contains("quote_removed"));
    }

    [Fact]
    public async Task ShareLinks_CarryTheCapAndLockoutBackstops()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await using var read = new NpgsqlCommand(
            """SELECT count(*) FROM pg_constraint WHERE conname IN ('ShareLinks_ViewsUsed_ck', 'ShareLinks_WrongPasswordCount_ck')""", db);

        // Act
        var count = await read.ExecuteScalarAsync(Ct);

        // Assert
        count.Should().Be(2L);
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
             WHERE table_schema = 'public' AND table_name IN ('DisclosureAuthorizations', 'ShareLinks', 'ShareAccesses')
            UNION ALL
            SELECT conname || ' ' || pg_get_constraintdef(oid) FROM pg_constraint
             WHERE conrelid IN ('"DisclosureAuthorizations"'::regclass, '"ShareLinks"'::regclass, '"ShareAccesses"'::regclass, '"AuditEntries"'::regclass)
            UNION ALL
            SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('DisclosureAuthorizations', 'ShareLinks', 'ShareAccesses')
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
