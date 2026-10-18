using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-13-note-review.sql against the previous schema and the bootstrap
// script (design/specs/2026-10-13-note-review-and-signing-design.md §3).
[Collection(SharedAppHostCollection.Name)]
public class NoteReviewMigrationTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoteReviewMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
    {
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using (var db = await old.OpenAsync(Ct))
        {
            await RunMigrationAsync(db, "2026-10-03-patient-accounts.sql");
            await RunMigrationAsync(db, "2026-10-04-patient-invitations.sql");
            await RunMigrationAsync(db, "2026-10-07-appointment-notifications.sql");
            await RunMigrationAsync(db, "2026-10-10-live-session.sql");
            await RunMigrationAsync(db, "2026-10-12-note-structuring.sql");
            await RunMigrationAsync(db, "2026-10-13-note-review.sql");
            await RunMigrationAsync(db, "2026-10-13-note-review.sql");
            await RunMigrationAsync(db, "2026-10-17-note-pdf.sql"); // the bootstrap script already includes it
        }
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using (var db = await fresh.OpenAsync(Ct))
        {
            await RunMigrationAsync(db, "2026-10-13-note-review.sql");
        }

        var oldShape = await ShapeAsync(old);
        var freshShape = await ShapeAsync(fresh);
        oldShape.Should().BeEquivalentTo(freshShape);
        freshShape.Should().Contain(["Notes.SignedByCrp", "NoteSections.Revision"]);
    }

    [Fact]
    public async Task SignedNote_WithoutCrp_IsRejected()
    {
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, "2026-10-13-note-review.sql");
        await using var command = new NpgsqlCommand("""
            WITH p AS (INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name")
                       VALUES (gen_random_uuid(), 'x@test.local', gen_random_uuid(), 'X') RETURNING "Id"),
                 pa AS (INSERT INTO "Patients" ("ProviderId","FirstName","LastName")
                        SELECT "Id", 'A', 'B' FROM p RETURNING "Id", "ProviderId")
            INSERT INTO "Notes" ("PatientId","ProviderId","Status","InputMode","SignedAt")
            SELECT "Id", "ProviderId", 'Signed', 'VoiceLive', now() FROM pa
            """, db);

        var act = () => command.ExecuteNonQueryAsync(Ct);

        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("Notes_SignedByCrp_ck");
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
        string[] tables = ["Notes", "NoteSections", "AuditEntries"];
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
