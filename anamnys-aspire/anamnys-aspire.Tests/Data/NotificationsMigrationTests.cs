using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-07-appointment-notifications.sql against the previous schema and the
// bootstrap script (design/specs/2026-10-07-appointment-notifications-design.md §3).
[Collection(SharedAppHostCollection.Name)]
public class NotificationsMigrationTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NotificationsMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
    {
        // Previous schema = the pre-split fixture plus the earlier migrations; then this migration twice.
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using (var db = await old.OpenAsync(Ct))
        {
            await RunMigrationAsync(db, "2026-10-03-patient-accounts.sql");
            await RunMigrationAsync(db, "2026-10-04-patient-invitations.sql");
            await RunMigrationAsync(db, "2026-10-07-appointment-notifications.sql");
            await RunMigrationAsync(db, "2026-10-07-appointment-notifications.sql");
        }
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using (var db = await fresh.OpenAsync(Ct))
        {
            await RunMigrationAsync(db, "2026-10-07-appointment-notifications.sql"); // no-op on the new bootstrap
        }

        // Assert — both databases have the same columns and constraints for the touched tables.
        var oldShape = await ShapeAsync(old);
        var freshShape = await ShapeAsync(fresh);
        oldShape.Should().BeEquivalentTo(freshShape);
        freshShape.Should().Contain([
            "AppointmentConfirmations.DeadlineAt", "AppointmentConfirmationTokens.TokenHash",
            "BookingPolicies.AutoCancelMode", "BookingPolicies.AutoCancelHours",
            "Reminders.Attempts", "Reminders.ConfirmationId"]);
    }

    private static async Task RunMigrationAsync(NpgsqlConnection db, string file)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", file), Ct);
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(Ct);
    }

    // "table.column" for every column, plus constraint definitions and index names, of the touched tables.
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
        string[] tables = ["AppointmentConfirmations", "AppointmentConfirmationTokens", "BookingPolicies", "Reminders", "Notifications"];
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
