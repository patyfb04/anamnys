using Anamnys.Tests.Retention;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-11-04-patient-dossier.sql against the previous schema and the bootstrap script, a legacy row,
// and the constraints (design/specs/2026-11-04-patient-dossier-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class PatientDossierMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-11-04-patient-dossier.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
        "2026-10-27-medication-timeline.sql", "2026-10-28-retention-panel.sql", "2026-10-29-theme-absence.sql", "2026-10-30-plan-adherence.sql",
        "2026-10-30-patient-quotes.sql", "2026-10-31-record-search.sql", "2026-11-01-protected-sharing.sql", "2026-11-03-integrity-chain.sql",
    ];

    [Fact]
    public async Task PatientDossierMigration_FromPreviousSchema_RunTwice_AgreesWithTheBootstrapSchema()
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
            await RunMigrationAsync(db, "2026-11-05-backup-runs.sql"); // the bootstrap script already includes it
            await RunMigrationAsync(db, "2026-11-09-availability.sql"); // the bootstrap script already includes it
            await RunMigrationAsync(db, "2026-11-10-appointment-series.sql"); // the bootstrap script already includes it
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
        freshShape.Should().Contain("Dossiers.GeneratedAt timestamp with time zone YES ");
        freshShape.Should().Contain("Dossiers.ProviderId uuid NO ");
        freshShape.Should().Contain("Dossiers.Status text NO 'queued'::text");
        freshShape.Should().Contain(s => s.StartsWith("Dossiers_ProviderId_fkey") && s.Contains("Providers"));
        freshShape.Should().Contain(s => s.StartsWith("Dossiers_PatientId_fkey") && s.Contains("Patients"));
        freshShape.Should().Contain(s => s.StartsWith("Dossiers_Provider_Number_key") && s.Contains("UNIQUE"));
        freshShape.Should().Contain(s => s.Contains("Dossiers_Patient_active_key") && s.Contains("UNIQUE") && s.Contains("queued"));
        freshShape.Should().Contain(s => s.StartsWith("AuditEntries_subject_ck") && s.Contains("dossier_requested")
            && s.Contains("dossier_downloaded") && s.Contains("dossier_receipt_downloaded") && s.Contains("integrity_check_failed"));
    }

    [Fact]
    public async Task PatientDossierMigration_LegacyRows_GetTheirProviderANumberAndAStateTheirFileAllows()
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
        await ExecuteAsync(db,
            """
            INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@provider, @provider::text || '@test.local', gen_random_uuid(), 'X');
            INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@patient, @provider, 'Ana', 'Silva');
            INSERT INTO "Dossiers" ("PatientId","GeneratedAt","ObjectKey") VALUES (@patient, '2026-01-01T12:00:00Z', 'dossiers/a.zip');
            INSERT INTO "Dossiers" ("PatientId","GeneratedAt") VALUES (@patient, '2026-02-01T12:00:00Z');
            """, ("provider", provider), ("patient", patient));

        // Act
        await RunMigrationAsync(db, MigrationFile);
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        await using var command = new NpgsqlCommand(
            """
            SELECT "ProviderId", "Number", "Status", "Reason", "ReasonNote" IS NOT NULL, "RequestedAt" = "GeneratedAt", "ExpiredAt" IS NOT NULL
              FROM "Dossiers" ORDER BY "Number"
            """, db);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(Guid Provider, int Number, string Status, string Reason, bool Note, bool Requested, bool Expired)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6)));
        }
        rows.Should().Equal(
            (provider, 1, "done", "other", true, true, false),
            (provider, 2, "expired", "other", true, true, true));
    }

    private static async Task InsertAsync(
        RetentionScratch s, Guid provider, Guid patient, int number, string status = "queued", string reason = "crp", string? note = null,
        string? from = null, string? to = null, bool generated = false, string? objectKey = null, string? failure = null)
    {
        await s.ExecuteAsync(
            """
            INSERT INTO "Dossiers" ("ProviderId","PatientId","Number","Status","Reason","ReasonNote","PeriodFrom","PeriodTo","RequestedAt",
                                    "GeneratedAt","ObjectKey","FailureReason")
            VALUES (@p, @pa, @n, @status, @reason, @note, @from::date, @to::date, now(), CASE WHEN @generated THEN now() END, @key, @failure)
            """, Ct, ("p", provider), ("pa", patient), ("n", number), ("status", status), ("reason", reason),
            ("note", (object?)note ?? DBNull.Value), ("from", (object?)from ?? DBNull.Value), ("to", (object?)to ?? DBNull.Value),
            ("generated", generated), ("key", (object?)objectKey ?? DBNull.Value), ("failure", (object?)failure ?? DBNull.Value));
    }

    [Fact]
    public async Task Dossiers_OneQueuedOrRunningPerPatient_AndNumbersUniquePerProvider()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var other = await s.AddPatientAsync(provider, "Bia", Ct);
        var otherProvider = await s.AddProviderAsync(Ct);
        var stranger = await s.AddPatientAsync(otherProvider, "Cia", Ct);
        await InsertAsync(s, provider, patient, 1);

        // Act
        var running = async () => await InsertAsync(s, provider, patient, 9, status: "running");
        var sameNumber = async () => await InsertAsync(s, provider, other, 1);
        await InsertAsync(s, provider, patient, 3, status: "failed", failure: "dossier_error");
        await InsertAsync(s, provider, other, 2);
        await InsertAsync(s, otherProvider, stranger, 1);

        // Assert
        await running.Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_Patient_active_key*");
        await sameNumber.Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_Provider_Number_key*");
        (await s.CountAsync("""SELECT count(*) FROM "Dossiers" """, Ct)).Should().Be(4);
    }

    [Fact]
    public async Task Dossiers_ReasonPeriodStateAndAuditConstraints()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var n = 0;
        Func<Task> Insert(string status = "failed", string reason = "crp", string? note = null, string? from = null, string? to = null,
            bool generated = false, string? objectKey = null, string? failure = "dossier_error") =>
            async () => await InsertAsync(s, provider, patient, ++n, status, reason, note, from, to, generated, objectKey, failure);

        // Act and Assert
        await Insert(reason: "judicial").Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_Reason_ck*");
        await Insert(reason: "other").Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_ReasonNote_ck*");
        await Insert(reason: "crp", note: "x").Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_ReasonNote_ck*");
        await Insert(reason: "other", note: new string('a', 201)).Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_ReasonNote_ck*");
        await Insert(from: "2026-10-02", to: "2026-10-01").Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_Period_ck*");
        await Insert(status: "done", failure: null).Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_Generated_ck*");
        await Insert(status: "failed", generated: true).Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_Generated_ck*");
        await Insert(status: "expired", generated: true, objectKey: "k", failure: null).Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_ObjectKey_ck*");
        await Insert(status: "paused").Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_Status_ck*");
        await Insert(failure: "boom").Should().ThrowAsync<PostgresException>().WithMessage("*Dossiers_FailureReason_ck*");
        await Insert(reason: "other", note: new string('a', 200), from: "2026-10-01", to: "2026-10-01").Should().NotThrowAsync();
        await Insert(status: "done", generated: true, objectKey: "k", failure: null).Should().NotThrowAsync();
        await Insert(status: "expired", generated: true, failure: null).Should().NotThrowAsync();
        foreach (var action in new[] { "dossier_requested", "dossier_downloaded", "dossier_receipt_downloaded" })
        {
            await s.ExecuteAsync(
                """INSERT INTO "AuditEntries" ("ActorId","Action","FieldChanged","NewValue","At") VALUES (@p, @a, 'dossier', 'x', now())""",
                Ct, ("p", provider), ("a", action));
        }
        (await s.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "Action" LIKE 'dossier_%'""", Ct)).Should().Be(3);
    }

    private static async Task RunMigrationAsync(NpgsqlConnection db, string file)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", file), Ct);
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(Ct);
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

    private static async Task<List<string>> ShapeAsync(ScratchDatabase scratch)
    {
        await using var db = await scratch.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT table_name || '.' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(column_default, '')
              FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name = 'Dossiers'
            UNION ALL
            SELECT conname || ' ' || pg_get_constraintdef(oid) FROM pg_constraint
             WHERE conrelid IN ('"Dossiers"'::regclass, '"AuditEntries"'::regclass)
            UNION ALL
            SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'Dossiers'
            UNION ALL
            SELECT 'trigger ' || tgname || ' ' || tgtype FROM pg_trigger WHERE tgrelid = '"Dossiers"'::regclass AND NOT tgisinternal
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
