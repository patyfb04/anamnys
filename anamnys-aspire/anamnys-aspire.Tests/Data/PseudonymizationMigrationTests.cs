using Anamnys.Tests.Retention;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-11-17-pseudonymization.sql against the previous schema and the bootstrap script, plus the new
// constraints (design/specs/2026-11-17-pseudonymization-design.md, section 5).
[Collection(SharedAppHostCollection.Name)]
public class PseudonymizationMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-11-17-pseudonymization.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
        "2026-10-27-medication-timeline.sql", "2026-10-28-retention-panel.sql", "2026-10-29-theme-absence.sql", "2026-10-30-plan-adherence.sql",
        "2026-10-30-patient-quotes.sql", "2026-10-31-record-search.sql", "2026-11-01-protected-sharing.sql", "2026-11-03-integrity-chain.sql",
        "2026-11-04-patient-dossier.sql", "2026-11-05-backup-runs.sql", "2026-11-09-availability.sql", "2026-11-10-appointment-series.sql",
        "2026-11-12-structured-intake.sql", "2026-11-15-recording-consent.sql", "2026-11-16-transient-audio.sql",
    ];

    private static readonly string[] Tables = ["PatientPseudonymSecrets", "PseudonymTokens", "PseudonymizationKeys", "PseudonymizationBackfills", "Notes"];

    [Fact]
    public async Task PseudonymizationMigration_FromPreviousSchema_RunTwice_AgreesWithTheBootstrapSchema_AndMarksOldNotesUnconverted()
    {
        // Arrange: a note with a transcript written before the migration.
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        var note = Guid.NewGuid();
        await using (var db = await old.OpenAsync(Ct))
        {
            foreach (var file in PreviousMigrations)
            {
                await RunMigrationAsync(db, file);
            }
            await ExecuteAsync(db,
                """
                INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'Dra. Teste');
                INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'Ana', 'Silva');
                INSERT INTO "Notes" ("Id","ProviderId","PatientId","Status","InputMode","Format","RawTranscript")
                VALUES (@n, @p, @pa, 'ReadyForReview', 'Text', 'DAP', 'Texto antigo.');
                """, ("p", Guid.NewGuid()), ("pa", Guid.NewGuid()), ("n", note));
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
        bool converted;
        await using (var db = await old.OpenAsync(Ct))
        {
            await using var command = new NpgsqlCommand("""SELECT "TranscriptPseudonymized" FROM "Notes" WHERE "Id" = @n""", db);
            command.Parameters.AddWithValue("n", note);
            converted = (bool)(await command.ExecuteScalarAsync(Ct))!;
        }

        // Assert
        oldShape.Should().BeEquivalentTo(freshShape);
        converted.Should().BeFalse();
        freshShape.Should().Contain("PatientPseudonymSecrets.Protected bytea NO ");
        freshShape.Should().Contain("PseudonymTokens.Fingerprint bytea NO ");
        freshShape.Should().Contain("PseudonymTokens.Number integer NO ");
        freshShape.Should().Contain("Notes.TranscriptPseudonymized boolean NO false");
        freshShape.Should().Contain("Notes.PseudonymizationFailedAt timestamp with time zone YES ");
        freshShape.Should().Contain("PseudonymizationBackfills.Total integer NO 0");
        // The secrets' own Data Protection key ring lives with them in Postgres (one XML element per friendly name).
        freshShape.Should().Contain("PseudonymizationKeys.Xml text NO ");
        freshShape.Should().Contain("PseudonymizationKeys.FriendlyName text NO ");
        freshShape.Should().Contain(s => s.StartsWith("PseudonymizationKeys_FriendlyName_key") && s.Contains("UNIQUE (\"FriendlyName\")"));
        // Only ids, kinds, fingerprints and numbers: no text column but the (checked) kind could hold a name.
        freshShape.Where(s => (s.StartsWith("PseudonymTokens.") || s.StartsWith("PatientPseudonymSecrets.")) && s.Contains(" text "))
            .Should().Equal("PseudonymTokens.Kind text NO ");
        freshShape.Should().Contain(s => s.StartsWith("PseudonymTokens_Kind_ck"));
        freshShape.Should().Contain(s => s.StartsWith("PseudonymTokens_Number_key") && s.Contains("UNIQUE (\"PatientId\", \"Kind\", \"Number\")"));
        freshShape.Should().Contain(s => s.StartsWith("PseudonymTokens_PatientId_fkey") && s.Contains("CASCADE"));
        freshShape.Should().Contain(s => s.StartsWith("Notes_FailureReason_ck") && s.Contains("pseudonymization_failed"));
        freshShape.Should().Contain("trigger PseudonymTokens_patient_not_discarded");
        freshShape.Should().Contain("trigger PatientPseudonymSecrets_patient_not_discarded");
        freshShape.Should().Contain(s => s.StartsWith("AuditEntries_subject_ck") && s.Contains("pseudonymization_backfill_started")
            && s.Contains("recording_source_switched"));
    }

    [Fact]
    public async Task Tokens_RejectBadRows_CascadeWithThePatient_AndTheNewReasonAndActionsAreAccepted()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var discarded = await s.AddPatientAsync(provider, "Bia", Ct, discardedAt: DateTimeOffset.UtcNow);
        var fingerprint = new byte[32];
        fingerprint[0] = 1;
        const string insert = """INSERT INTO "PseudonymTokens" ("PatientId","Kind","Fingerprint","Number") VALUES (@pa, @k, @f, @n)""";
        Task Insert(Guid pa, string kind, byte[] f, int n) => s.ExecuteAsync(insert, Ct, ("pa", pa), ("k", kind), ("f", f), ("n", n));

        // Act
        await Insert(patient, "person", fingerprint, 1);
        await Insert(patient, "organization", fingerprint, 1);
        var sameNumber = async () => await Insert(patient, "person", new byte[32], 1);
        var sameFingerprint = async () => await Insert(patient, "person", fingerprint, 2);
        var address = async () => await Insert(patient, "address", new byte[32], 1);
        var zero = async () => await Insert(patient, "school", new byte[32], 0);
        var shortFingerprint = async () => await Insert(patient, "school", new byte[8], 1);
        var afterDiscard = async () => await Insert(discarded, "person", fingerprint, 1);
        var secretAfterDiscard = async () => await s.ExecuteAsync(
            """INSERT INTO "PatientPseudonymSecrets" ("PatientId","Protected") VALUES (@pa, '\x01')""", Ct, ("pa", discarded));
        await s.ExecuteAsync("""INSERT INTO "PatientPseudonymSecrets" ("PatientId","Protected") VALUES (@pa, '\x01')""", Ct, ("pa", patient));
        var note = await s.AddNoteAsync(provider, patient, DateTimeOffset.UtcNow, Ct);
        await s.ExecuteAsync(
            """UPDATE "Notes" SET "Status" = 'Failed', "FailureReason" = 'pseudonymization_failed', "PseudonymizationFailedAt" = now() WHERE "Id" = @n""",
            Ct, ("n", note));
        await s.ExecuteAsync(
            """INSERT INTO "AuditEntries" ("ActorId","Action","FieldChanged","NewValue","At") VALUES (@p, 'pseudonymization_backfill_started', 'provider', '3', now())""",
            Ct, ("p", provider));
        await s.ExecuteAsync("""INSERT INTO "PseudonymizationBackfills" ("ProviderId","StartedAt","Total") VALUES (@p, now(), 3)""", Ct, ("p", provider));
        var negative = async () => await s.ExecuteAsync("""UPDATE "PseudonymizationBackfills" SET "Done" = -1 WHERE "ProviderId" = @p""", Ct, ("p", provider));

        // Assert
        await sameNumber.Should().ThrowAsync<PostgresException>().WithMessage("*PseudonymTokens_Number_key*");
        await sameFingerprint.Should().ThrowAsync<PostgresException>().WithMessage("*PseudonymTokens_pkey*");
        await address.Should().ThrowAsync<PostgresException>().WithMessage("*PseudonymTokens_Kind_ck*");
        await zero.Should().ThrowAsync<PostgresException>().WithMessage("*PseudonymTokens_Number_ck*");
        await shortFingerprint.Should().ThrowAsync<PostgresException>().WithMessage("*PseudonymTokens_Fingerprint_ck*");
        await afterDiscard.Should().ThrowAsync<PostgresException>().WithMessage("*was discarded*");
        await secretAfterDiscard.Should().ThrowAsync<PostgresException>().WithMessage("*was discarded*");
        await negative.Should().ThrowAsync<PostgresException>().WithMessage("*PseudonymizationBackfills_Counts_ck*");
        (await s.CountAsync("""SELECT count(*) FROM "Notes" WHERE "FailureReason" = 'pseudonymization_failed'""", Ct)).Should().Be(1);
        await s.ExecuteAsync("""DELETE FROM "Notes" WHERE "PatientId" = @pa""", Ct, ("pa", patient));
        await s.ExecuteAsync("""DELETE FROM "Patients" WHERE "Id" = @pa""", Ct, ("pa", patient));
        (await s.CountAsync("""SELECT count(*) FROM "PseudonymTokens" WHERE "PatientId" = @pa""", Ct, ("pa", patient))).Should().Be(0);
        (await s.CountAsync("""SELECT count(*) FROM "PatientPseudonymSecrets" WHERE "PatientId" = @pa""", Ct, ("pa", patient))).Should().Be(0);
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
        var tables = string.Join(", ", Tables.Select(t => $"'{t}'"));
        var regclasses = string.Join(", ", Tables.Append("AuditEntries").Select(t => $"'\"{t}\"'::regclass"));
        await using var command = new NpgsqlCommand($"""
            SELECT table_name || '.' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(column_default, '')
              FROM information_schema.columns
             WHERE table_schema = 'public' AND table_name IN ({tables})
            UNION ALL
            SELECT conname || ' ' || pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid IN ({regclasses})
            UNION ALL
            SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ({tables})
            UNION ALL
            SELECT 'trigger ' || tgname FROM pg_trigger WHERE tgrelid IN ({regclasses}) AND NOT tgisinternal
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
