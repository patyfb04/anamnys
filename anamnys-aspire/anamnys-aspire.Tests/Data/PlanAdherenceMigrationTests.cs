using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-30-plan-adherence.sql against the previous schema and the bootstrap script
// (design/specs/2026-10-30-plan-adherence-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class PlanAdherenceMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-10-30-plan-adherence.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
        "2026-10-27-medication-timeline.sql", "2026-10-28-retention-panel.sql", "2026-10-29-theme-absence.sql",
    ];

    [Fact]
    public async Task PlanAdherenceMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
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
            await RunMigrationAsync(db, "2026-10-28-retention-panel.sql");
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
        freshShape.Should().Contain("FollowUpItems.ProviderId uuid NO ");
        freshShape.Should().Contain("FollowUpItems.ExcerptRefId uuid YES ");
        freshShape.Should().Contain("FollowUpItems.ResolvedById uuid YES ");
        freshShape.Should().Contain(s => s.Contains("FollowUpItems_Patient_fk") && s.Contains("ON DELETE CASCADE"));
        freshShape.Should().Contain(s => s.Contains("FollowUpItems_ExcerptRefId_fkey") && s.Contains("ON DELETE SET NULL"));
        freshShape.Should().Contain(s => s.Contains("FollowUpItems_Patient_Resolved_idx"));
        freshShape.Should().Contain("trigger FollowUpItems_patient_not_discarded");
        freshShape.Should().Contain(s => s.Contains("AuditEntries_subject_ck") && s.Contains("follow_up_reopened") && s.Contains("plan_reviewed"));
    }

    [Fact]
    public async Task PlanAdherenceMigration_BackfillsTheProviderOfExistingFollowUps()
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
        await ExecuteAsync(db, """
            INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'P');
            INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'Ana', 'Silva');
            INSERT INTO "FollowUpItems" ("PatientId","Description") VALUES (@pa, 'x');
            """, ("p", provider), ("pa", patient));

        // Act
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        (await ScalarAsync(db, """SELECT "ProviderId" FROM "FollowUpItems" WHERE "PatientId" = @pa""", ("pa", patient))).Should().Be(provider);
    }

    [Fact]
    public async Task AuditEntries_AcceptThePlanAndFollowUpActionsWithoutANote_AndStillRefuseOthers()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);

        // Act
        foreach (var action in new[] { "plan_reviewed", "follow_up_created", "follow_up_resolved", "follow_up_reopened" })
        {
            await ExecuteAsync(db, """INSERT INTO "AuditEntries" ("Action") VALUES (@a)""", ("a", action));
        }
        var other = () => ExecuteAsync(db, """INSERT INTO "AuditEntries" ("Action") VALUES ('note_signed')""");

        // Assert
        (await ScalarAsync(db, """SELECT count(*) FROM "AuditEntries" """)).Should().Be(4L);
        (await other.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task FollowUpItems_RefuseANewRowForADiscardedRecord_AndAProviderThatIsNotThePatients()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var provider = Guid.NewGuid();
        var other = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var discarded = Guid.NewGuid();
        await ExecuteAsync(db, """
            INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'P'),
                                                                                (@o, @o::text || '@test.local', gen_random_uuid(), 'O');
            INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'Ana', 'Silva');
            INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName","DiscardedAt") VALUES (@d, @p, 'Bia', 'Souza', now());
            """, ("p", provider), ("o", other), ("pa", patient), ("d", discarded));

        // Act
        var onDiscarded = () => ExecuteAsync(db, """INSERT INTO "FollowUpItems" ("PatientId","ProviderId","Description") VALUES (@d, @p, 'x')""", ("d", discarded), ("p", provider));
        var foreign = () => ExecuteAsync(db, """INSERT INTO "FollowUpItems" ("PatientId","ProviderId","Description") VALUES (@pa, @o, 'x')""", ("pa", patient), ("o", other));

        // Assert
        (await onDiscarded.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("ANDIS");
        (await foreign.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");
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
             WHERE table_schema = 'public' AND table_name = 'FollowUpItems'
            UNION ALL
            SELECT c.conrelid::regclass::text || ':' || c.conname || ' ' || pg_get_constraintdef(c.oid)
              FROM pg_constraint c WHERE c.conrelid::regclass::text IN ('"FollowUpItems"', '"AuditEntries"')
            UNION ALL
            SELECT indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'FollowUpItems'
            UNION ALL
            SELECT 'trigger ' || t.tgname FROM pg_trigger t WHERE t.tgrelid = '"FollowUpItems"'::regclass AND NOT t.tgisinternal
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
