using Anamnys.Tests.Retention;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-11-12-structured-intake.sql against the previous schema and the bootstrap script, plus the
// new constraints and the default template seed (design/specs/2026-11-12-structured-intake-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class StructuredIntakeMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-11-12-structured-intake.sql";
    private const string DefaultTemplate = "0f310000-0000-4000-8000-000000000001";
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
    ];

    private static readonly string[] Tables =
        ["IntakeTemplates", "IntakeQuestions", "IntakeResponses", "Intakes", "IntakeTransfers", "ProviderIntakeSettings", "Notes", "MedicationEntries"];

    [Fact]
    public async Task IntakeMigration_FromPreviousSchema_RunTwice_AgreesWithTheBootstrapSchema()
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
        }
        await using var fresh = await ScratchDatabase.CreateAsync(fixture, Ct, "anamnys-db-script.sql");
        await using (var db = await fresh.OpenAsync(Ct))
        {
            await RunMigrationAsync(db, MigrationFile);
        }

        // Act
        var oldShape = await ShapeAsync(old);
        var freshShape = await ShapeAsync(fresh);
        var oldSeed = await SeedAsync(old);
        var freshSeed = await SeedAsync(fresh);

        // Assert
        oldShape.Should().BeEquivalentTo(freshShape);
        freshShape.Should().Contain("IntakeTemplates.ProviderId uuid YES ");
        freshShape.Should().Contain("IntakeTemplates.IsDefault boolean NO false");
        freshShape.Should().Contain("IntakeTemplates.ArchivedAt timestamp with time zone YES ");
        freshShape.Should().Contain("IntakeQuestions.SectionKey text NO ");
        freshShape.Should().Contain("IntakeQuestions.Choices ARRAY YES ");
        freshShape.Should().Contain("IntakeQuestions.Disabled boolean NO false");
        freshShape.Should().Contain("IntakeResponses.IntakeId uuid NO ");
        freshShape.Should().Contain("Intakes.Status text NO 'draft'::text");
        freshShape.Should().Contain("IntakeTransfers.ExcerptRefId uuid YES ");
        freshShape.Should().Contain("ProviderIntakeSettings.DefaultTemplateId uuid NO ");
        freshShape.Should().Contain("Notes.IntakeId uuid YES ");
        freshShape.Should().Contain("MedicationEntries.SourceNoteId uuid YES ");
        freshShape.Should().Contain(s => s.StartsWith("MedicationEntries_SourceNoteId_fkey") && s.Contains("Notes") && s.Contains("SET NULL"));
        freshShape.Should().Contain(s => s.StartsWith("Notes_Format_ck") && s.Contains("Anamnese"));
        freshShape.Should().Contain(s => s.StartsWith("IntakeQuestions_TargetField_ck") && s.Contains("plan.objective"));
        freshShape.Should().Contain(s => s.StartsWith("IntakeResponses_IntakeId_fkey") && s.Contains("CASCADE"));
        freshShape.Should().Contain(s => s.StartsWith("Notes_IntakeId_fkey") && s.Contains("SET NULL"));
        freshShape.Should().Contain(s => s.Contains("Intakes_OneDraft_key") && s.Contains("UNIQUE"));
        freshShape.Should().Contain(s => s.Contains("IntakeTemplates_OneDefault_key") && s.Contains("UNIQUE"));
        freshShape.Should().Contain("trigger Intakes_patient_not_discarded");
        freshShape.Should().Contain(s => s.StartsWith("AuditEntries_subject_ck") && s.Contains("intake_started") && s.Contains("intake_signed")
            && s.Contains("intake_transfer_confirmed") && s.Contains("intake_transfer_ignored") && s.Contains("intake_template_adapted")
            && s.Contains("intake_discarded") && s.Contains("intake_answers_unlocked")
            && s.Contains("series_ended"));
        oldSeed.Should().Equal(freshSeed);
        freshSeed.Should().HaveCount(36);
        freshSeed.Select(q => q.Split(' ')[0]).Distinct().Should().Equal("identificacao", "queixa", "historia", "saude", "contexto", "objetivos");
    }

    [Fact]
    public async Task Intakes_RejectBadRowsAndSecondDrafts_AndAuditAcceptsTheNewActions()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var discarded = await s.AddPatientAsync(provider, "Bia", Ct, discardedAt: DateTimeOffset.UtcNow);
        const string intake = """
            INSERT INTO "Intakes" ("ProviderId","PatientId","TemplateId") VALUES (@p, @pa, '0f310000-0000-4000-8000-000000000001')
            """;

        // Act
        await s.ExecuteAsync(intake, Ct, ("p", provider), ("pa", patient));
        var secondDraft = async () => await s.ExecuteAsync(intake, Ct, ("p", provider), ("pa", patient));
        var onDiscarded = async () => await s.ExecuteAsync(intake, Ct, ("p", provider), ("pa", discarded));
        var secondDefault = async () => await s.ExecuteAsync(
            """INSERT INTO "IntakeTemplates" ("ProviderId","Name","IsDefault") VALUES (NULL, 'Outro', true)""", Ct);
        var ownerless = async () => await s.ExecuteAsync(
            """INSERT INTO "IntakeTemplates" ("ProviderId","Name","IsDefault") VALUES (NULL, 'Outro', false)""", Ct);
        var choiceWithout = async () => await s.ExecuteAsync(
            """
            INSERT INTO "IntakeQuestions" ("TemplateId","SectionKey","SectionTitle","Key","Label","AnswerType")
            VALUES ('0f310000-0000-4000-8000-000000000001', 'x', 'X', 'nova', 'Nova', 'choice')
            """, Ct);
        var badTarget = async () => await s.ExecuteAsync(
            """
            INSERT INTO "IntakeQuestions" ("TemplateId","SectionKey","SectionTitle","Key","Label","TargetField")
            VALUES ('0f310000-0000-4000-8000-000000000001', 'x', 'X', 'nova', 'Nova', 'patient.name')
            """, Ct);
        var signedWithoutDate = async () => await s.ExecuteAsync(
            """UPDATE "Intakes" SET "Status" = 'signed' WHERE "PatientId" = @pa""", Ct, ("pa", patient));
        var anamnese = Guid.NewGuid();
        await s.ExecuteAsync(
            """INSERT INTO "Notes" ("Id","ProviderId","PatientId","Status","InputMode","Format") VALUES (@id, @p, @pa, 'ReadyForReview', 'Text', 'Anamnese')""",
            Ct, ("id", anamnese), ("p", provider), ("pa", patient));
        foreach (var action in new[] { "intake_started", "intake_signed", "intake_transfer_confirmed", "intake_transfer_ignored", "intake_template_adapted", "intake_discarded", "intake_answers_unlocked" })
        {
            await s.ExecuteAsync(
                """INSERT INTO "AuditEntries" ("ActorId","Action","FieldChanged","NewValue","At") VALUES (@p, @a, 'intake', 'x', now())""",
                Ct, ("p", provider), ("a", action));
        }

        // Assert
        await secondDraft.Should().ThrowAsync<PostgresException>().WithMessage("*Intakes_OneDraft_key*");
        await onDiscarded.Should().ThrowAsync<PostgresException>().WithMessage("*was discarded*");
        await secondDefault.Should().ThrowAsync<PostgresException>().WithMessage("*IntakeTemplates_OneDefault_key*");
        await ownerless.Should().ThrowAsync<PostgresException>().WithMessage("*IntakeTemplates_Owner_ck*");
        await choiceWithout.Should().ThrowAsync<PostgresException>().WithMessage("*IntakeQuestions_Choices_ck*");
        await badTarget.Should().ThrowAsync<PostgresException>().WithMessage("*IntakeQuestions_TargetField_ck*");
        await signedWithoutDate.Should().ThrowAsync<PostgresException>().WithMessage("*Intakes_Signed_ck*");
        (await s.CountAsync("""SELECT count(*) FROM "Notes" WHERE "Format" = 'Anamnese'""", Ct)).Should().Be(1);
        (await s.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "ActorId" = @p""", Ct, ("p", provider))).Should().Be(7);
    }

    private static async Task RunMigrationAsync(NpgsqlConnection db, string file)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", file), Ct);
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<List<string>> SeedAsync(ScratchDatabase scratch)
    {
        await using var db = await scratch.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"""
            SELECT q."SectionKey" || ' ' || q."Key" || ' ' || q."AnswerType" || ' ' || coalesce(q."TargetField", '-') || ' ' || q."Required"::text
              FROM "IntakeQuestions" q JOIN "IntakeTemplates" t ON t."Id" = q."TemplateId"
             WHERE t."Id" = '{DefaultTemplate}' AND t."IsDefault" AND t."ProviderId" IS NULL AND t."Name" = 'Anamnese padrão'
             ORDER BY q."Position"
            """, db);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
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
