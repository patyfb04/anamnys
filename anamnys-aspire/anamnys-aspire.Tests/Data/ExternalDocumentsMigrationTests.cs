using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-25-external-documents.sql against the previous schema and the bootstrap
// script (design/specs/2026-10-25-external-documents-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class ExternalDocumentsMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-10-25-external-documents.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExternalDocumentsMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using (var db = await old.OpenAsync(Ct))
        {
            await RunPreviousMigrationsAsync(db);
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, "2026-10-26-document-facts.sql"); // the bootstrap script already includes it
            await RunMigrationAsync(db, "2026-10-27-medication-timeline.sql"); // the bootstrap script already includes it
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
        freshShape.Should().Contain([
            "ExternalDocuments.Kind", "ExternalDocuments.DocumentDate", "ExternalDocuments.Description",
            "ExternalDocuments.ScannedAt", "ExternalDocuments.ScanAttempts", "ExternalDocuments.DeletedAt"]);
        freshShape.Should().Contain(s => s.StartsWith("ExternalDocuments:ExternalDocuments_Provider_Uploaded_idx"));
        freshShape.Should().Contain(s => s.Contains("ExternalDocuments_Patient_fk"));
    }

    [Fact]
    public async Task ExternalDocumentsMigration_WithLegacyRows_GivesThemKindOutro_AndSurvivesBeingRunTwice()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        await RunPreviousMigrationsAsync(db);
        var (provider, patient) = (Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(db, """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'P')""", ("p", provider));
        await ExecuteAsync(db, """INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'A', 'B')""", ("pa", patient), ("p", provider));
        await ExecuteAsync(db, """
            INSERT INTO "ExternalDocuments" ("PatientId","ProviderId","Filename","MimeType","ObjectKey","ByteSize")
            VALUES (@pa, @p, 'x.pdf', 'application/pdf', 'external/legacy', 10)
            """, ("pa", patient), ("p", provider));

        // Act
        await RunMigrationAsync(db, MigrationFile);
        var first = await KindAsync(db);
        await ExecuteAsync(db, """UPDATE "ExternalDocuments" SET "Kind" = 'laudo'""");
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        first.Should().Be("outro");
        (await KindAsync(db)).Should().Be("laudo");
    }

    [Theory]
    [InlineData("'whatever'", "NULL", "'pending'", "'external/a'", "NULL", false)]
    [InlineData("'laudo'", "repeat('x', 201)", "'pending'", "'external/a'", "NULL", false)]
    [InlineData("'laudo'", "repeat('x', 200)", "'pending'", "'external/a'", "NULL", true)]
    [InlineData("'laudo'", "NULL", "'pending'", "NULL", "NULL", false)]
    [InlineData("'laudo'", "NULL", "'clean'", "NULL", "NULL", false)]
    [InlineData("'laudo'", "NULL", "'infected'", "NULL", "NULL", true)]
    [InlineData("'laudo'", "NULL", "'clean'", "NULL", "now()", true)]
    public async Task ExternalDocuments_Rows_AreCheckedForKindDescriptionAndMissingObject(
        string kind, string description, string status, string objectKey, string deletedAt, bool accepted)
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var (provider, patient) = await SeedPatientAsync(db);

        // Act
        var act = () => ExecuteAsync(db, $"""
            INSERT INTO "ExternalDocuments" ("PatientId","ProviderId","Filename","MimeType","ObjectKey","ByteSize","ScanStatus","Kind","Description","DeletedAt")
            VALUES (@pa, @p, 'x.pdf', 'application/pdf', {objectKey}, 1, {status}, {kind}, {description}, {deletedAt})
            """, ("pa", patient), ("p", provider));

        // Assert
        if (accepted)
        {
            await act.Should().NotThrowAsync();
        }
        else
        {
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        }
    }

    [Fact]
    public async Task ExternalDocuments_ObjectKeys_StayUniqueWhenPresent_AndRepeatWhenNull()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var (provider, patient) = await SeedPatientAsync(db);
        const string head = """INSERT INTO "ExternalDocuments" ("PatientId","ProviderId","Filename","MimeType","ObjectKey","ByteSize","ScanStatus","Kind") VALUES """;

        // Act
        var twoWithoutKey = () => ExecuteAsync(db,
            head + "(@pa, @p, 'x.pdf', 'application/pdf', NULL, 1, 'infected', 'outro'), (@pa, @p, 'y.pdf', 'application/pdf', NULL, 1, 'infected', 'outro')",
            ("pa", patient), ("p", provider));
        var sameKey = () => ExecuteAsync(db,
            head + "(@pa, @p, 'x.pdf', 'application/pdf', 'external/same', 1, 'pending', 'outro'), (@pa, @p, 'y.pdf', 'application/pdf', 'external/same', 1, 'pending', 'outro')",
            ("pa", patient), ("p", provider));

        // Assert
        await twoWithoutKey.Should().NotThrowAsync();
        (await sameKey.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
    }

    [Fact]
    public async Task ExternalDocuments_OtherProvidersPatient_IsRejected()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var (_, patient) = await SeedPatientAsync(db);
        var other = Guid.NewGuid();
        await ExecuteAsync(db, """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'O')""", ("p", other));

        // Act
        var act = () => ExecuteAsync(db, """
            INSERT INTO "ExternalDocuments" ("PatientId","ProviderId","Filename","MimeType","ObjectKey","ByteSize","Kind")
            VALUES (@pa, @p, 'x.pdf', 'application/pdf', 'external/o', 1, 'outro')
            """, ("pa", patient), ("p", other));

        // Assert
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");
    }

    [Theory]
    [InlineData("external_document_uploaded", true)]
    [InlineData("external_document_opened", true)]
    [InlineData("external_document_deleted", true)]
    [InlineData("external_document_blocked", true)]
    [InlineData("account_export_requested", true)]
    [InlineData("external_document_whatever", false)]
    public async Task AuditEntries_WithoutNoteOrDocument_AllowTheExternalDocumentActions(string action, bool allowed)
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

    private static async Task<string?> KindAsync(NpgsqlConnection db)
    {
        await using var command = new NpgsqlCommand("""SELECT "Kind" FROM "ExternalDocuments" """, db);
        return (string?)await command.ExecuteScalarAsync(Ct);
    }

    private static async Task<(Guid Provider, Guid Patient)> SeedPatientAsync(NpgsqlConnection db)
    {
        var (provider, patient) = (Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(db, """INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'P')""", ("p", provider));
        await ExecuteAsync(db, """INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'A', 'B')""", ("pa", patient), ("p", provider));
        return (provider, patient);
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
        await RunMigrationAsync(db, "2026-10-24-excerpt-refs.sql");
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
        string[] tables = ["ExternalDocuments", "AuditEntries"];
        command.Parameters.AddWithValue("tables", tables);
        command.Parameters.AddWithValue("quoted", tables.Select(t => $"\"{t}\"").ToArray());
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var shape = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            var line = reader.GetString(0);
            // Later migrations own the full action list of this constraint (2026-10-26-document-facts.sql).
            if (!line.Contains("AuditEntries_subject_ck"))
            {
                shape.Add(line);
            }
        }
        return shape;
    }
}
