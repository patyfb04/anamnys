using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-28-retention-panel.sql against the previous schema and the bootstrap script
// (design/specs/2026-10-28-retention-panel-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class RetentionPanelMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-10-28-retention-panel.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
        "2026-10-27-medication-timeline.sql",
    ];

    [Fact]
    public async Task RetentionPanelMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
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
        freshShape.Should().Contain([
            "Patients.LastRecordAt", "Patients.RetentionUntil", "Patients.DiscardedAt", "Providers.UnsignedNoteDays",
            "Notifications.DedupeKey", "DisposalTerms.ProviderId", "DisposalTerms.PatientId", "DisposalTerms.CreatedAt",
            "DisposalTerms.ObjectKey", "DisposalTerms.PeriodFrom", "DisposalTerms.PeriodTo", "DisposalTerms.GuardYears",
            "DisposalTerms.Basis", "DisposalTerms.Counts", "DisposalTerms.RetentionUntil"]);
        freshShape.Should().Contain(s => s.Contains("Patients_Provider_RetentionUntil_idx"));
        freshShape.Should().Contain(s => s.Contains("Notifications_Provider_DedupeKey_key") && s.Contains("UNIQUE"));
        freshShape.Should().Contain(s => s.Contains("DisposalTerms_Patient_fk") && s.Contains("ON DELETE RESTRICT"));
        freshShape.Should().Contain(s => s.Contains("Providers_UnsignedNoteDays_ck"));
        freshShape.Should().Contain(s => s.Contains("patient_discarded"));
    }

    [Fact]
    public async Task Providers_UnsignedNoteDays_DefaultsToSeven_AndRefusesZero()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var (provider, _) = await SeedPatientAsync(db);

        // Act
        var days = await ScalarAsync(db, """SELECT "UnsignedNoteDays" FROM "Providers" WHERE "Id" = @p""", ("p", provider));
        var zero = () => ExecuteAsync(db, """UPDATE "Providers" SET "UnsignedNoteDays" = 0 WHERE "Id" = @p""", ("p", provider));

        // Assert
        days.Should().Be(7);
        (await zero.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task Notifications_DedupeKey_IsUniquePerProvider_AndOptional()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var (provider, _) = await SeedPatientAsync(db);
        var (other, _) = await SeedPatientAsync(db);
        const string insert = """
            INSERT INTO "Notifications" ("ProviderId","Kind","Channel","ScheduledFor","DeliveryStatus","DedupeKey")
            VALUES (@p, 'unsigned_note', 'in_app', now(), 'sent', @k)
            """;

        // Act
        await ExecuteAsync(db, insert, ("p", provider), ("k", "unsigned:1"));
        await ExecuteAsync(db, insert, ("p", other), ("k", "unsigned:1"));
        await ExecuteAsync(db, insert, ("p", provider), ("k", DBNull.Value));
        await ExecuteAsync(db, insert, ("p", provider), ("k", DBNull.Value));
        var duplicate = () => ExecuteAsync(db, insert, ("p", provider), ("k", "unsigned:1"));

        // Assert
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task DisposalTerms_KeepThePatientRow_OnePerPatient_AndGuardYearsAtLeastFive()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var (provider, patient) = await SeedPatientAsync(db);
        const string insert = """
            INSERT INTO "DisposalTerms" ("ProviderId","PatientId","ObjectKey","PeriodFrom","PeriodTo","GuardYears","Basis","Counts","RetentionUntil")
            VALUES (@p, @pa, 'disposal/x.pdf', '2018-01-01', '2020-01-01', @g, 'Res. CFP 001/2009', '{"notes": 3}', '2025-01-01')
            """;

        // Act
        var tooShort = () => ExecuteAsync(db, insert, ("p", provider), ("pa", patient), ("g", 4));
        await ExecuteAsync(db, insert, ("p", provider), ("pa", patient), ("g", 5));
        var second = () => ExecuteAsync(db, insert, ("p", provider), ("pa", patient), ("g", 5));
        var deletePatient = () => ExecuteAsync(db, """DELETE FROM "Patients" WHERE "Id" = @pa""", ("pa", patient));

        // Assert
        (await tooShort.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        (await second.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        (await deletePatient.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23001"); // restrict_violation
    }

    [Theory]
    [InlineData("patient_discarded", true)]
    [InlineData("medication_from_fact", true)]
    [InlineData("fact_confirmed", true)]
    [InlineData("account_export_requested", true)]
    [InlineData("patient_whatever", false)]
    public async Task AuditEntries_WithoutNoteOrDocument_AllowTheDiscardAction(string action, bool allowed)
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

    [Fact]
    public async Task EarlierMigrations_ReRunAfterThisOne_DoNotNarrowTheAuditActions()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        await RunPreviousMigrationsAsync(db);
        await RunMigrationAsync(db, MigrationFile);

        // Act
        await RunMigrationAsync(db, "2026-10-27-medication-timeline.sql");
        await RunMigrationAsync(db, "2026-10-26-document-facts.sql");
        await RunMigrationAsync(db, "2026-10-25-external-documents.sql");

        // Assert
        var act = () => ExecuteAsync(db, """INSERT INTO "AuditEntries" ("ActorId","Action") VALUES (gen_random_uuid(), 'patient_discarded')""");
        await act.Should().NotThrowAsync();
    }

    private const string TriggersSql = """
        SELECT c.relname || ':' || t.tgname || ' ' || pg_get_triggerdef(t.oid)
          FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
         WHERE NOT t.tgisinternal AND t.tgname LIKE '%patient_not_discarded'
         ORDER BY 1
        """;

    [Fact]
    public async Task DiscardedTriggers_FromPreviousSchema_AndOnTheBootstrapSchema_Agree_AndRefuseNewRows()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var oldDb = await old.OpenAsync(Ct);
        await RunPreviousMigrationsAsync(oldDb);
        await RunMigrationAsync(oldDb, MigrationFile);
        await RunMigrationAsync(oldDb, MigrationFile);
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var freshDb = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(freshDb, MigrationFile);

        // Act
        var oldTriggers = await ListAsync(oldDb, TriggersSql);
        var freshTriggers = await ListAsync(freshDb, TriggersSql);
        var (provider, patient) = await SeedPatientAsync(oldDb);
        await ExecuteAsync(oldDb, """UPDATE "Patients" SET "DiscardedAt" = now() WHERE "Id" = @pa""", ("pa", patient));
        var act = () => ExecuteAsync(oldDb,
            """INSERT INTO "Sessions" ("PatientId","ProviderId") VALUES (@pa, @p)""", ("pa", patient), ("p", provider));

        // Assert
        oldTriggers.Should().Equal(freshTriggers);
        oldTriggers.Should().HaveCount(18);
        oldTriggers.Should().Contain(t => t.StartsWith("Notes:Notes_patient_not_discarded") && t.Contains("BEFORE INSERT"));
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("ANDIS");
    }

    [Fact]
    public async Task DisposalTermsFromTheFirstVersion_GetRetentionUntil_BackFilled()
    {
        // Arrange: the first version of this file created DisposalTerms without RetentionUntil.
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        await RunPreviousMigrationsAsync(db);
        await RunMigrationAsync(db, MigrationFile);
        await ExecuteAsync(db, """ALTER TABLE "DisposalTerms" DROP COLUMN "RetentionUntil" """);
        var (provider, patient) = await SeedPatientAsync(db);
        await ExecuteAsync(db,
            """
            INSERT INTO "DisposalTerms" ("ProviderId","PatientId","ObjectKey","PeriodFrom","PeriodTo","GuardYears","Basis")
            VALUES (@p, @pa, 'disposal/x.pdf', '2019-01-01', '2020-02-29', 6, 'Res. CFP 001/2009')
            """, ("p", provider), ("pa", patient));

        // Act
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        (await ScalarAsync(db, """SELECT "RetentionUntil"::text FROM "DisposalTerms" WHERE "PatientId" = @pa""", ("pa", patient)))
            .Should().Be("2026-02-28");
        (await ScalarAsync(db, """SELECT is_nullable FROM information_schema.columns WHERE table_name = 'DisposalTerms' AND column_name = 'RetentionUntil'"""))
            .Should().Be("NO");
    }

    private static async Task<List<string>> ListAsync(NpgsqlConnection db, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }

    private static async Task<(Guid Provider, Guid Patient)> SeedPatientAsync(NpgsqlConnection db)
    {
        var (provider, patient) = (Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(db, """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'P')""", ("p", provider));
        await ExecuteAsync(db, """INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'A', 'B')""", ("pa", patient), ("p", provider));
        return (provider, patient);
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

    private static async Task RunPreviousMigrationsAsync(NpgsqlConnection db)
    {
        foreach (var file in PreviousMigrations)
        {
            await RunMigrationAsync(db, file);
        }
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
             WHERE table_schema = 'public' AND table_name = ANY (@tables)
            UNION ALL
            SELECT table_name || '.' || column_name
              FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = ANY (@tables)
            UNION ALL
            SELECT c.conrelid::regclass::text || ':' || c.conname || ' ' || pg_get_constraintdef(c.oid)
              FROM pg_constraint c WHERE c.conrelid::regclass::text = ANY (@quoted)
            UNION ALL
            SELECT tablename || ':' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = ANY (@tables)
            """, db);
        string[] tables = ["Patients", "Providers", "Notifications", "DisposalTerms", "RetentionRules", "AuditEntries"];
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
