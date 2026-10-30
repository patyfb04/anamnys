using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-27-medication-timeline.sql against the previous schema and the bootstrap script
// (design/specs/2026-10-27-medication-timeline-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class MedicationTimelineMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-10-27-medication-timeline.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
    ];

    [Fact]
    public async Task MedicationTimelineMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using (var db = await old.OpenAsync(Ct))
        {
            await RunPreviousMigrationsAsync(db);
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, "2026-10-28-retention-panel.sql"); // the bootstrap script already includes it
            await RunMigrationAsync(db, "2026-10-29-theme-absence.sql"); // the bootstrap script already includes it
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
        freshShape.Should().Contain(["MedicationEntries.TreatmentId", "MedicationEntries.CreatedAt", "MedicationEntries.FromDocument", "MedicationEntries.SourceFactId"]);
        freshShape.Should().Contain(s => s.Contains("MedicationEntries_SourceFactId_fkey") && s.Contains("ON DELETE SET NULL"));
        freshShape.Should().Contain(s => s.Contains("MedicationEntries_Treatment_idx"));
        freshShape.Should().Contain(s => s.Contains("medication_from_fact"));
    }

    [Fact]
    public async Task MedicationTimelineMigration_WithExistingRecords_GivesEachItsOwnTreatment_AndKeepsThemOnReRun()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        await RunPreviousMigrationsAsync(db);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var patient = await SeedPatientAsync(db);
        await ExecuteAsync(db, """
            INSERT INTO "MedicationEntries" ("Id","PatientId","Drug","StartedOn") VALUES (@a, @p, 'Sertralina', '2026-01-01'), (@b, @p, 'Lítio', '2026-02-01')
            """, ("a", a), ("b", b), ("p", patient));

        // Act
        await RunMigrationAsync(db, MigrationFile);
        var first = await TreatmentsAsync(db);
        await ExecuteAsync(db, """UPDATE "MedicationEntries" SET "TreatmentId" = @a WHERE "Id" = @b""", ("a", a), ("b", b));
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        first.Should().BeEquivalentTo(new Dictionary<Guid, Guid> { [a] = a, [b] = b });
        (await TreatmentsAsync(db))[b].Should().Be(a);
    }

    [Fact]
    public async Task MedicationEntries_InsertWithoutTreatment_GetsItsOwn_AndSourceFactIsClearedWithTheFact()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var provider = Guid.NewGuid();
        var patient = await SeedPatientAsync(db, provider);
        var (document, fact, entry) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(db, """
            INSERT INTO "ExternalDocuments" ("Id","PatientId","ProviderId","Filename","MimeType","ObjectKey","ByteSize","ScanStatus","Kind")
            VALUES (@d, @pa, @p, 'x.pdf', 'application/pdf', 'external/x', 1, 'clean', 'laudo');
            INSERT INTO "ExtractedFacts" ("Id","ExternalDocumentId","ProviderId","Kind","Value") VALUES (@f, @d, @p, 'medication', 'Sertralina');
            INSERT INTO "MedicationEntries" ("Id","PatientId","Drug","StartedOn","SourceFactId") VALUES (@e, @pa, 'Sertralina', '2026-01-01', @f);
            """, ("d", document), ("pa", patient), ("p", provider), ("f", fact), ("e", entry));

        // Act
        var treatment = (Guid)(await ScalarAsync(db, """SELECT "TreatmentId" FROM "MedicationEntries" WHERE "Id" = @e""", ("e", entry)))!;
        var duplicate = () => ExecuteAsync(db, """INSERT INTO "MedicationEntries" ("PatientId","Drug","StartedOn","SourceFactId") VALUES (@pa, 'X', '2026-01-01', @f)""", ("pa", patient), ("f", fact));
        var failure = await duplicate.Should().ThrowAsync<PostgresException>();
        await ExecuteAsync(db, """DELETE FROM "ExtractedFacts" WHERE "Id" = @f""", ("f", fact));
        var source = await ScalarAsync(db, """SELECT "SourceFactId" FROM "MedicationEntries" WHERE "Id" = @e""", ("e", entry));

        // Assert
        treatment.Should().NotBeEmpty();
        failure.Which.SqlState.Should().Be("23505");
        source.Should().Be(DBNull.Value);
    }

    [Theory]
    [InlineData("medication_from_fact", true)]
    [InlineData("fact_confirmed", true)]
    [InlineData("external_document_uploaded", true)]
    [InlineData("account_export_requested", true)]
    [InlineData("medication_whatever", false)]
    public async Task AuditEntries_WithoutNoteOrDocument_AllowTheMedicationAction(string action, bool allowed)
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
        await RunMigrationAsync(db, "2026-10-26-document-facts.sql");
        await RunMigrationAsync(db, "2026-10-25-external-documents.sql");

        // Assert
        var act = () => ExecuteAsync(db, """INSERT INTO "AuditEntries" ("ActorId","Action") VALUES (gen_random_uuid(), 'medication_from_fact')""");
        await act.Should().NotThrowAsync();
    }

    private static async Task<Dictionary<Guid, Guid>> TreatmentsAsync(NpgsqlConnection db)
    {
        await using var command = new NpgsqlCommand("""SELECT "Id", "TreatmentId" FROM "MedicationEntries" """, db);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new Dictionary<Guid, Guid>();
        while (await reader.ReadAsync(Ct))
        {
            rows[reader.GetGuid(0)] = reader.GetGuid(1);
        }
        return rows;
    }

    private static async Task<Guid> SeedPatientAsync(NpgsqlConnection db, Guid? providerId = null)
    {
        var (provider, patient) = (providerId ?? Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(db, """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'P')""", ("p", provider));
        await ExecuteAsync(db, """INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'A', 'B')""", ("pa", patient), ("p", provider));
        return patient;
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
            SELECT table_name || '.' || column_name
              FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = ANY (@tables)
            UNION ALL
            SELECT c.conrelid::regclass::text || ':' || c.conname || ' ' || pg_get_constraintdef(c.oid)
              FROM pg_constraint c WHERE c.conrelid::regclass::text = ANY (@quoted)
            UNION ALL
            SELECT tablename || ':' || indexname || ' ' || indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = ANY (@tables)
            """, db);
        string[] tables = ["MedicationEntries", "AuditEntries"];
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
