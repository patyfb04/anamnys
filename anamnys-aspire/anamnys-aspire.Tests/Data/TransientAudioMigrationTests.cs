using Anamnys.Tests.Retention;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-11-16-transient-audio.sql against the previous schema and the bootstrap script, plus the new
// constraints (design/specs/2026-11-16-transient-audio-tab-capture-design.md, section 2.2).
[Collection(SharedAppHostCollection.Name)]
public class TransientAudioMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-11-16-transient-audio.sql";
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
        "2026-11-12-structured-intake.sql", "2026-11-15-recording-consent.sql",
    ];

    private static readonly string[] Tables = ["AudioDestructionLogs"];

    [Fact]
    public async Task TransientAudioMigration_FromPreviousSchema_RunTwice_AgreesWithTheBootstrapSchema_AndFillsOldRows()
    {
        // Arrange: a destruction row written before the migration points at a session and has no provider or patient yet.
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        var provider = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var session = Guid.NewGuid();
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
                INSERT INTO "Sessions" ("Id","PatientId","ProviderId") VALUES (@s, @pa, @p);
                INSERT INTO "AudioDestructionLogs" ("SessionId","Outcome") VALUES (@s, 'transcribed');
                """, ("p", provider), ("pa", patient), ("s", session));
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
        long filled;
        await using (var db = await old.OpenAsync(Ct))
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*) FROM "AudioDestructionLogs"
                 WHERE "ProviderId" = @p AND "PatientId" = @pa AND "RecordingMode" = 'session' AND "Sources" = ARRAY['in_person']
                """, db);
            command.Parameters.AddWithValue("p", provider);
            command.Parameters.AddWithValue("pa", patient);
            filled = (long)(await command.ExecuteScalarAsync(Ct))!;
        }

        // Assert
        oldShape.Should().BeEquivalentTo(freshShape);
        filled.Should().Be(1);
        freshShape.Should().Contain("AudioDestructionLogs.ProviderId uuid NO ");
        freshShape.Should().Contain("AudioDestructionLogs.PatientId uuid NO ");
        freshShape.Should().Contain("AudioDestructionLogs.NoteId uuid YES ");
        freshShape.Should().Contain("AudioDestructionLogs.RecordingMode text NO ");
        freshShape.Should().Contain("AudioDestructionLogs.Sources ARRAY NO ");
        freshShape.Should().NotContain(s => s.StartsWith("AudioDestructionLogs_SessionId_fkey"));
        freshShape.Should().Contain(s => s.StartsWith("AudioDestructionLogs_Outcome_ck") && s.Contains("abandoned"));
        freshShape.Should().Contain(s => s.StartsWith("AudioDestructionLogs_Patient_fk") && s.Contains("CASCADE"));
        freshShape.Should().Contain(s => s.StartsWith("AudioDestructionLogs_NoteId_fkey") && s.Contains("SET NULL"));
        freshShape.Should().Contain(s => s.Contains("AudioDestructionLogs_NoteId_idx"));
        freshShape.Should().Contain("trigger AudioDestructionLogs_patient_not_discarded");
        freshShape.Should().Contain(s => s.StartsWith("AuditEntries_subject_ck") && s.Contains("audio_destroyed")
            && s.Contains("recording_source_switched") && s.Contains("consent_document_downloaded"));
    }

    [Fact]
    public async Task DestructionLogs_RejectBadRows_AndAuditAcceptsTheNewAction()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var other = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var discarded = await s.AddPatientAsync(provider, "Bia", Ct, discardedAt: DateTimeOffset.UtcNow);
        var note = await s.AddNoteAsync(provider, patient, DateTimeOffset.UtcNow, Ct);
        const string insert =
            """
            INSERT INTO "AudioDestructionLogs" ("SessionId","ProviderId","PatientId","NoteId","RecordingMode","Sources","Outcome")
            VALUES (gen_random_uuid(), @p, @pa, @n, @mode, @src, @out)
            """;
        Task Insert(Guid p, Guid pa, object n, string mode, string[] sources, string outcome) =>
            s.ExecuteAsync(insert, Ct, ("p", p), ("pa", pa), ("n", n), ("mode", mode), ("src", sources), ("out", outcome));

        // Act
        await Insert(provider, patient, note, "session", ["in_person", "online_capture", "online_speaker"], "transcribed");
        await Insert(provider, patient, DBNull.Value, "dictation", ["in_person"], "abandoned");
        await Insert(provider, patient, note, "session", ["online_speaker"], "failed");
        var badOutcome = async () => await Insert(provider, patient, note, "session", ["in_person"], "lost");
        var badMode = async () => await Insert(provider, patient, note, "meeting", ["in_person"], "failed");
        var unknownSource = async () => await Insert(provider, patient, note, "session", ["in_person", "phone"], "failed");
        var noSource = async () => await Insert(provider, patient, note, "session", [], "failed");
        var onlineDictation = async () => await Insert(provider, patient, note, "dictation", ["online_capture"], "failed");
        var foreign = async () => await Insert(other, patient, DBNull.Value, "session", ["in_person"], "abandoned");
        var afterDiscard = async () => await Insert(provider, discarded, DBNull.Value, "session", ["in_person"], "abandoned");
        await s.ExecuteAsync(
            """INSERT INTO "AuditEntries" ("ActorId","Action","FieldChanged","NewValue","At") VALUES (@p, 'audio_destroyed', 'recording', 'x', now())""",
            Ct, ("p", provider));
        await s.ExecuteAsync("""DELETE FROM "Notes" WHERE "Id" = @n""", Ct, ("n", note));

        // Assert
        await badOutcome.Should().ThrowAsync<PostgresException>().WithMessage("*AudioDestructionLogs_Outcome_ck*");
        await badMode.Should().ThrowAsync<PostgresException>().WithMessage("*AudioDestructionLogs_RecordingMode_ck*");
        await unknownSource.Should().ThrowAsync<PostgresException>().WithMessage("*AudioDestructionLogs_Sources_ck*");
        await noSource.Should().ThrowAsync<PostgresException>().WithMessage("*AudioDestructionLogs_Sources_ck*");
        await onlineDictation.Should().ThrowAsync<PostgresException>().WithMessage("*AudioDestructionLogs_Sources_ck*");
        await foreign.Should().ThrowAsync<PostgresException>().WithMessage("*AudioDestructionLogs_Patient_fk*");
        await afterDiscard.Should().ThrowAsync<PostgresException>().WithMessage("*was discarded*");
        (await s.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "ActorId" = @p AND "Action" = 'audio_destroyed'""", Ct, ("p", provider))).Should().Be(1);
        // The note's deletion keeps its rows, without the note.
        (await s.CountAsync("""SELECT count(*) FROM "AudioDestructionLogs" WHERE "PatientId" = @pa AND "NoteId" IS NULL""", Ct, ("pa", patient))).Should().Be(3);
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
