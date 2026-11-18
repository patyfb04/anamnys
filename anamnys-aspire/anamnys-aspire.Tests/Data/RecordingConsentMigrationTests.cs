using Anamnys.Tests.Retention;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-11-15-recording-consent.sql against the previous schema and the bootstrap script, plus the new
// constraints (design/specs/2026-11-15-recording-consent-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class RecordingConsentMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-11-15-recording-consent.sql";
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
        "2026-11-12-structured-intake.sql",
    ];

    private static readonly string[] Tables = ["RecordingConsents", "ConsentEvents"];

    [Fact]
    public async Task ConsentMigration_FromPreviousSchema_RunTwice_AgreesWithTheBootstrapSchema_AndFillsTheProvider()
    {
        // Arrange: a consent row written before the migration has no provider yet.
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        var provider = Guid.NewGuid();
        var patient = Guid.NewGuid();
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
                INSERT INTO "RecordingConsents" ("PatientId","State") VALUES (@pa, 'NOT_REQUESTED');
                """, ("p", provider), ("pa", patient));
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, MigrationFile);
            await RunMigrationAsync(db, "2026-11-16-transient-audio.sql"); // the bootstrap script already includes it
        }
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using (var db = await fresh.OpenAsync(Ct))
        {
            await RunMigrationAsync(db, MigrationFile);
        }

        // Act
        var oldShape = await ShapeAsync(old);
        var freshShape = await ShapeAsync(fresh);
        long filled;
        await using (var db = await old.OpenAsync(Ct))
        {
            await using var command = new NpgsqlCommand("""SELECT count(*) FROM "RecordingConsents" WHERE "ProviderId" = @p""", db);
            command.Parameters.AddWithValue("p", provider);
            filled = (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        // Assert
        oldShape.Should().BeEquivalentTo(freshShape);
        filled.Should().Be(1);
        freshShape.Should().Contain("RecordingConsents.ProviderId uuid NO ");
        freshShape.Should().Contain("RecordingConsents.TermText text YES ");
        freshShape.Should().Contain("RecordingConsents.SignTokenHash bytea YES ");
        freshShape.Should().Contain("RecordingConsents.RevokeTokenHash bytea YES ");
        freshShape.Should().Contain("RecordingConsents.FailedBirthDateCount integer NO 0");
        freshShape.Should().Contain("RecordingConsents.DocumentObjectKey text YES ");
        freshShape.Should().Contain("RecordingConsents.TreatmentPlanId uuid YES ");
        freshShape.Should().Contain("ConsentEvents.ActorKind text NO 'system'::text");
        freshShape.Should().Contain(s => s.StartsWith("RecordingConsents_Patient_fk") && s.Contains("CASCADE"));
        freshShape.Should().Contain(s => s.Contains("RecordingConsents_Patient_active_key") && s.Contains("UNIQUE") && s.Contains("SIGNED"));
        freshShape.Should().Contain(s => s.Contains("RecordingConsents_SignTokenHash_key") && s.Contains("UNIQUE"));
        freshShape.Should().Contain(s => s.Contains("RecordingConsents_RevokeTokenHash_key") && s.Contains("UNIQUE"));
        freshShape.Should().Contain(s => s.StartsWith("AuditEntries_subject_ck") && s.Contains("consent_sent") && s.Contains("consent_signed")
            && s.Contains("consent_revoked") && s.Contains("consent_expired") && s.Contains("consent_assent_recorded")
            && s.Contains("dictation_confirmed") && s.Contains("consent_document_downloaded") && s.Contains("intake_answers_unlocked"));
    }

    [Fact]
    public async Task Consents_RejectBadRowsAndSecondActive_AndAuditAcceptsTheNewActions()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var other = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        const string consent = """INSERT INTO "RecordingConsents" ("PatientId","ProviderId","State") VALUES (@pa, @p, @st)""";

        // Act
        await s.ExecuteAsync(consent, Ct, ("pa", patient), ("p", provider), ("st", "SENT"));
        await s.ExecuteAsync(consent, Ct, ("pa", patient), ("p", provider), ("st", "EXPIRED"));
        var secondActive = async () => await s.ExecuteAsync(consent, Ct, ("pa", patient), ("p", provider), ("st", "SENT"));
        var foreign = async () => await s.ExecuteAsync(consent, Ct, ("pa", patient), ("p", other), ("st", "EXPIRED"));
        var badRole = async () => await s.ExecuteAsync(
            """UPDATE "RecordingConsents" SET "SignerRole" = 'friend' WHERE "PatientId" = @pa""", Ct, ("pa", patient));
        var badExpiry = async () => await s.ExecuteAsync(
            """UPDATE "RecordingConsents" SET "ExpiredReason" = 'whatever' WHERE "PatientId" = @pa""", Ct, ("pa", patient));
        var tooManyFailures = async () => await s.ExecuteAsync(
            """UPDATE "RecordingConsents" SET "FailedBirthDateCount" = 6 WHERE "PatientId" = @pa""", Ct, ("pa", patient));
        var longNote = async () => await s.ExecuteAsync(
            """UPDATE "RecordingConsents" SET "ProviderNote" = repeat('a', 1001) WHERE "PatientId" = @pa""", Ct, ("pa", patient));
        var badActor = async () => await s.ExecuteAsync(
            """INSERT INTO "ConsentEvents" ("ConsentId","FromState","ToState","ActorKind") SELECT "Id", 'SENT', 'SIGNED', 'patient' FROM "RecordingConsents" WHERE "PatientId" = @pa LIMIT 1""",
            Ct, ("pa", patient));
        var freeTextReason = async () => await s.ExecuteAsync(
            """INSERT INTO "ConsentEvents" ("ConsentId","FromState","ToState","ActorKind","Reason") SELECT "Id", 'SENT', 'REVOKED', 'signer', 'Não quero mais' FROM "RecordingConsents" WHERE "PatientId" = @pa LIMIT 1""",
            Ct, ("pa", patient));
        foreach (var action in new[] { "consent_sent", "consent_signed", "consent_revoked", "consent_expired", "consent_assent_recorded", "dictation_confirmed", "consent_document_downloaded" })
        {
            await s.ExecuteAsync(
                """INSERT INTO "AuditEntries" ("ActorId","Action","FieldChanged","NewValue","At") VALUES (@p, @a, 'recording_consent', 'x', now())""",
                Ct, ("p", provider), ("a", action));
        }

        // Assert
        await secondActive.Should().ThrowAsync<PostgresException>().WithMessage("*RecordingConsents_Patient_active_key*");
        await foreign.Should().ThrowAsync<PostgresException>().WithMessage("*RecordingConsents_Patient_fk*");
        await badRole.Should().ThrowAsync<PostgresException>().WithMessage("*RecordingConsents_SignerRole_ck*");
        await badExpiry.Should().ThrowAsync<PostgresException>().WithMessage("*RecordingConsents_ExpiredReason_ck*");
        await tooManyFailures.Should().ThrowAsync<PostgresException>().WithMessage("*RecordingConsents_FailedBirthDateCount_ck*");
        await longNote.Should().ThrowAsync<PostgresException>().WithMessage("*RecordingConsents_Lengths_ck*");
        await badActor.Should().ThrowAsync<PostgresException>().WithMessage("*ConsentEvents_ActorKind_ck*");
        await freeTextReason.Should().ThrowAsync<PostgresException>().WithMessage("*ConsentEvents_Reason_ck*");
        (await s.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "ActorId" = @p""", Ct, ("p", provider))).Should().Be(7);
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
