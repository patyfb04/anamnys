using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-10-26-document-facts.sql against the previous schema and the bootstrap script
// (design/specs/2026-10-26-document-facts-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class DocumentFactsMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-10-26-document-facts.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DocumentFactsMigration_FromPreviousSchema_AndOnTheBootstrapSchema_Agree()
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
            "ExternalDocuments.ReadStatus", "ExternalDocuments.ReadAttempts", "ExternalDocuments.ReadAt",
            "ExternalDocuments.PageCount", "ExternalDocuments.PagesTruncated", "ExternalDocuments.ExtractAttempts",
            "ExternalDocumentPages.ExternalDocumentId", "ExternalDocumentPages.ProviderId", "ExternalDocumentPages.PageNumber",
            "ExternalDocumentPages.Text", "ExternalDocumentPages.FromOcr", "ExternalDocumentPages.CreatedAt",
            "ExtractedFacts.ProviderId", "ExtractedFacts.Status", "ExtractedFacts.Quote", "ExtractedFacts.CreatedAt",
            "ExtractedFacts.DiscardedAt", "ExtractedFacts.CorrectedValue", "ExtractedFacts.PageRef", "ExtractedFacts.ExcerptRefId",
            "ExcerptRefs.PageNumber"]);
        freshShape.Should().Contain(s => s.StartsWith("ExternalDocumentPages:ExternalDocumentPages_Document_Page_key"));
        freshShape.Should().Contain(s => s.Contains("ExternalDocumentPages_ExternalDocumentId_fkey") && s.Contains("ON DELETE CASCADE"));
        freshShape.Should().Contain(s => s.Contains("ExtractedFacts_ProviderId_fkey"));
        freshShape.Should().Contain(s => s.Contains("ExcerptRefs_DocumentPage_ck"));
    }

    [Fact]
    public async Task DocumentFactsMigration_WithLegacyRows_QueuesCleanDocumentsOnce_AndDropsOwnerlessFactsAndPagelessRefs()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        await RunPreviousMigrationsAsync(db);
        var (provider, patient) = await SeedPatientAsync(db);
        var (clean, pending, deleted) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(db, """
            INSERT INTO "ExternalDocuments" ("Id","PatientId","ProviderId","Filename","MimeType","ObjectKey","ByteSize","ScanStatus","Kind","DeletedAt")
            VALUES (@c, @pa, @p, 'a.pdf', 'application/pdf', 'external/c', 1, 'clean', 'laudo', NULL),
                   (@q, @pa, @p, 'b.pdf', 'application/pdf', 'external/q', 1, 'pending', 'laudo', NULL),
                   (@d, @pa, @p, 'c.pdf', 'application/pdf', NULL, 1, 'clean', 'laudo', now())
            """, ("c", clean), ("q", pending), ("d", deleted), ("pa", patient), ("p", provider));
        await ExecuteAsync(db, """INSERT INTO "ExtractedFacts" ("ExternalDocumentId","Kind","Value") VALUES (@c, 'icd', 'F33.1')""", ("c", clean));
        await ExecuteAsync(db, """
            INSERT INTO "ExcerptRefs" ("ProviderId","SourceType","SourceId","Kind","DatedAt") VALUES (@p, 'document', @c, 'section', now())
            """, ("p", provider), ("c", clean));

        // Act
        await RunMigrationAsync(db, MigrationFile);
        var first = await ReadStatusesAsync(db);
        await ExecuteAsync(db, """UPDATE "ExternalDocuments" SET "ReadStatus" = 'done' WHERE "Id" = @c""", ("c", clean));
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        first.Should().BeEquivalentTo(new Dictionary<Guid, string?> { [clean] = "pending", [pending] = null, [deleted] = null });
        (await ReadStatusesAsync(db))[clean].Should().Be("done");
        (await CountAsync(db, """SELECT count(*) FROM "ExtractedFacts" """)).Should().Be(0);
        (await CountAsync(db, """SELECT count(*) FROM "ExcerptRefs" WHERE "SourceType" = 'document'""")).Should().Be(0);
    }

    [Theory]
    [InlineData("'clean'", "NULL", "NULL", true)]
    [InlineData("'clean'", "'pending'", "NULL", true)]
    [InlineData("'clean'", "'reading'", "0", true)]
    [InlineData("'clean'", "'done'", "3", true)]
    [InlineData("'clean'", "'failed'", "NULL", true)]
    [InlineData("'clean'", "'whatever'", "NULL", false)]
    [InlineData("'pending'", "'pending'", "NULL", false)]
    [InlineData("'failed'", "NULL", "NULL", true)]
    [InlineData("'clean'", "'done'", "-1", false)]
    public async Task ExternalDocuments_ReadColumns_AreChecked(string scanStatus, string readStatus, string pageCount, bool accepted)
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var (provider, patient) = await SeedPatientAsync(db);

        // Act
        var act = () => ExecuteAsync(db, $"""
            INSERT INTO "ExternalDocuments" ("PatientId","ProviderId","Filename","MimeType","ObjectKey","ByteSize","ScanStatus","Kind","ReadStatus","PageCount")
            VALUES (@pa, @p, 'x.pdf', 'application/pdf', 'external/x', 1, {scanStatus}, 'laudo', {readStatus}, {pageCount})
            """, ("pa", patient), ("p", provider));

        // Assert
        await AssertCheckAsync(act, accepted);
    }

    [Fact]
    public async Task ExternalDocumentPages_AreUniquePerPage_NumberedFromOne_AndGoWithTheDocument()
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var (provider, patient) = await SeedPatientAsync(db);
        var document = await AddDocumentAsync(db, provider, patient);
        const string insert = """INSERT INTO "ExternalDocumentPages" ("ExternalDocumentId","ProviderId","PageNumber","Text") VALUES (@d, @p, @n, 'texto')""";

        // Act
        await ExecuteAsync(db, insert, ("d", document), ("p", provider), ("n", 1));
        var duplicate = () => ExecuteAsync(db, insert, ("d", document), ("p", provider), ("n", 1));
        var zero = () => ExecuteAsync(db, insert, ("d", document), ("p", provider), ("n", 0));
        var duplicateFailure = await duplicate.Should().ThrowAsync<PostgresException>();
        var zeroFailure = await zero.Should().ThrowAsync<PostgresException>();
        await ExecuteAsync(db, """DELETE FROM "ExternalDocuments" WHERE "Id" = @d""", ("d", document));

        // Assert
        duplicateFailure.Which.SqlState.Should().Be("23505");
        zeroFailure.Which.SqlState.Should().Be("23514");
        (await CountAsync(db, """SELECT count(*) FROM "ExternalDocumentPages" """)).Should().Be(0);
    }

    [Theory]
    [InlineData("'pending'", "NULL", "NULL", "NULL", true)]
    [InlineData("'confirmed'", "NULL", "now()", "NULL", true)]
    [InlineData("'confirmed'", "NULL", "NULL", "NULL", false)]
    [InlineData("'corrected'", "'F32.1'", "now()", "NULL", true)]
    [InlineData("'corrected'", "NULL", "now()", "NULL", false)]
    [InlineData("'discarded'", "NULL", "NULL", "now()", true)]
    [InlineData("'discarded'", "NULL", "NULL", "NULL", false)]
    [InlineData("'whatever'", "NULL", "NULL", "NULL", false)]
    public async Task ExtractedFacts_Status_IsCheckedAgainstTheReviewColumns(
        string status, string correctedValue, string confirmedAt, string discardedAt, bool accepted)
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var (provider, patient) = await SeedPatientAsync(db);
        var document = await AddDocumentAsync(db, provider, patient);
        var confirmedBy = confirmedAt == "NULL" ? "NULL" : "@p";

        // Act
        var act = () => ExecuteAsync(db, $"""
            INSERT INTO "ExtractedFacts" ("ExternalDocumentId","ProviderId","Kind","Value","Status","CorrectedValue","ConfirmedBy","ConfirmedAt","DiscardedAt","Quote","PageRef")
            VALUES (@d, @p, 'icd', 'F33.1', {status}, {correctedValue}, {confirmedBy}, {confirmedAt}, {discardedAt}, 'CID-10 F33.1', 1)
            """, ("d", document), ("p", provider));

        // Assert
        await AssertCheckAsync(act, accepted);
    }

    [Theory]
    [InlineData("'document'", "NULL", "1", true)]
    [InlineData("'document'", "NULL", "NULL", false)]
    [InlineData("'document'", "NULL", "0", false)]
    [InlineData("'note'", "'subjective'", "NULL", true)]
    [InlineData("'note'", "'subjective'", "1", false)]
    [InlineData("'transcript'", "NULL", "NULL", true)]
    public async Task ExcerptRefs_DocumentReferences_PointAtAPage(string sourceType, string sectionKey, string pageNumber, bool accepted)
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);
        var (provider, _) = await SeedPatientAsync(db);

        // Act
        var act = () => ExecuteAsync(db, $"""
            INSERT INTO "ExcerptRefs" ("ProviderId","SourceType","SourceId","SectionKey","PageNumber","Kind","OffsetStart","OffsetEnd","Quote","DatedAt")
            VALUES (@p, {sourceType}, gen_random_uuid(), {sectionKey}, {pageNumber}, 'exact', 0, 5, 'trecho', now())
            """, ("p", provider));

        // Assert
        await AssertCheckAsync(act, accepted);
    }

    [Theory]
    [InlineData("fact_confirmed", true)]
    [InlineData("fact_corrected", true)]
    [InlineData("fact_discarded", true)]
    [InlineData("facts_extracted", true)]
    [InlineData("facts_deleted", true)]
    [InlineData("external_document_uploaded", true)]
    [InlineData("account_export_requested", true)]
    [InlineData("fact_whatever", false)]
    public async Task AuditEntries_WithoutNoteOrDocument_AllowTheFactActions(string action, bool allowed)
    {
        // Arrange
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using var db = await fresh.OpenAsync(Ct);
        await RunMigrationAsync(db, MigrationFile);

        // Act
        var act = () => ExecuteAsync(db, """INSERT INTO "AuditEntries" ("ActorId","Action") VALUES (gen_random_uuid(), @action)""", ("action", action));

        // Assert
        await AssertCheckAsync(act, allowed);
    }

    private static async Task AssertCheckAsync(Func<Task> act, bool accepted)
    {
        if (accepted)
        {
            await act.Should().NotThrowAsync();
        }
        else
        {
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        }
    }

    private static async Task<Dictionary<Guid, string?>> ReadStatusesAsync(NpgsqlConnection db)
    {
        await using var command = new NpgsqlCommand("""SELECT "Id", "ReadStatus" FROM "ExternalDocuments" """, db);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var statuses = new Dictionary<Guid, string?>();
        while (await reader.ReadAsync(Ct))
        {
            statuses[reader.GetGuid(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        return statuses;
    }

    private static async Task<long> CountAsync(NpgsqlConnection db, string sql)
    {
        await using var command = new NpgsqlCommand(sql, db);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<Guid> AddDocumentAsync(NpgsqlConnection db, Guid provider, Guid patient)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(db, """
            INSERT INTO "ExternalDocuments" ("Id","PatientId","ProviderId","Filename","MimeType","ObjectKey","ByteSize","ScanStatus","Kind")
            VALUES (@d, @pa, @p, 'x.pdf', 'application/pdf', 'external/' || @d::text, 1, 'clean', 'laudo')
            """, ("d", id), ("pa", patient), ("p", provider));
        return id;
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
        await RunMigrationAsync(db, "2026-10-25-external-documents.sql");
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
        string[] tables = ["ExternalDocuments", "ExternalDocumentPages", "ExtractedFacts", "ExcerptRefs", "AuditEntries"];
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
