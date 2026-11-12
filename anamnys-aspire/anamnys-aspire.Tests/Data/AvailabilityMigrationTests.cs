using Anamnys.Tests.Retention;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-11-09-availability.sql against the previous schema and the bootstrap script, plus the new
// constraints (design/specs/2026-11-09-availability-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class AvailabilityMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-11-09-availability.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
        "2026-10-27-medication-timeline.sql", "2026-10-28-retention-panel.sql", "2026-10-29-theme-absence.sql", "2026-10-30-plan-adherence.sql",
        "2026-10-30-patient-quotes.sql", "2026-10-31-record-search.sql", "2026-11-01-protected-sharing.sql", "2026-11-03-integrity-chain.sql",
        "2026-11-04-patient-dossier.sql", "2026-11-05-backup-runs.sql",
    ];

    [Fact]
    public async Task AvailabilityMigration_FromPreviousSchema_RunTwice_AgreesWithTheBootstrapSchema()
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
        freshShape.Should().Contain("ProviderHolidaySettings.WorksOnHolidays boolean NO false");
        freshShape.Should().Contain("ProviderHolidaySettings.DisabledHolidays ARRAY NO '{}'::text[]");
        freshShape.Should().Contain(s => s.StartsWith("ProviderHolidaySettings_ProviderId_fkey") && s.Contains("Providers") && s.Contains("CASCADE"));
        freshShape.Should().Contain(s => s.StartsWith("AvailabilityRules_ProviderId_fkey") && s.Contains("Providers"));
        freshShape.Should().Contain(s => s.StartsWith("AvailabilityExceptions_ProviderId_fkey") && s.Contains("Providers"));
        freshShape.Should().Contain("AvailabilityRules.VersionId uuid NO ");
        freshShape.Should().Contain("AvailabilityVersions.CreatedAt timestamp with time zone NO now()");
        freshShape.Should().Contain(s => s.StartsWith("AvailabilityRules_VersionId_fkey") && s.Contains("AvailabilityVersions") && s.Contains("CASCADE"));
        freshShape.Should().Contain(s => s.StartsWith("AvailabilityVersions_Provider_EffectiveFrom_key") && s.Contains("UNIQUE"));
        freshShape.Should().Contain(s => s.StartsWith("AvailabilityVersions_ProviderId_fkey") && s.Contains("Providers"));
        freshShape.Should().Contain(s => s.StartsWith("AvailabilityExceptions_Reason_ck") && s.Contains("200"));
        freshShape.Should().Contain(s => s.Contains("AvailabilityRules_Provider_EffectiveFrom_idx") && s.Contains("\"EffectiveFrom\""));
        freshShape.Should().Contain(s => s.Contains("AvailabilityExceptions_Provider_Range_idx") && s.Contains("\"StartsAt\""));
        freshShape.Should().Contain(s => s.StartsWith("AuditEntries_subject_ck") && s.Contains("availability_changed")
            && s.Contains("block_created") && s.Contains("block_deleted") && s.Contains("appointment_outside_availability")
            && s.Contains("dossier_receipt_downloaded"));
    }

    [Fact]
    public async Task Exceptions_RejectAReasonOver200Characters_AndAuditAcceptsTheNewActions()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);

        // Act
        var tooLong = async () => await s.ExecuteAsync(
            """INSERT INTO "AvailabilityExceptions" ("ProviderId","StartsAt","EndsAt","Kind","Reason") VALUES (@p, now(), now() + interval '1 hour', 'block', repeat('x', 201))""",
            Ct, ("p", provider));
        await s.ExecuteAsync(
            """INSERT INTO "AvailabilityExceptions" ("ProviderId","StartsAt","EndsAt","Kind","Reason") VALUES (@p, now(), now() + interval '1 hour', 'vacation', repeat('x', 200))""",
            Ct, ("p", provider));
        foreach (var action in new[] { "availability_changed", "block_created", "block_deleted", "appointment_outside_availability" })
        {
            await s.ExecuteAsync(
                """INSERT INTO "AuditEntries" ("ActorId","Action","FieldChanged","NewValue","At") VALUES (@p, @a, 'availability', 'x', now())""",
                Ct, ("p", provider), ("a", action));
        }
        await s.ExecuteAsync("""INSERT INTO "ProviderHolidaySettings" ("ProviderId") VALUES (@p)""", Ct, ("p", provider));

        // Assert
        await tooLong.Should().ThrowAsync<PostgresException>().WithMessage("*AvailabilityExceptions_Reason_ck*");
        (await s.CountAsync("""SELECT count(*) FROM "AvailabilityExceptions" WHERE "ProviderId" = @p""", Ct, ("p", provider))).Should().Be(1);
        (await s.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "ActorId" = @p""", Ct, ("p", provider))).Should().Be(4);
        (await s.CountAsync(
            """SELECT count(*) FROM "ProviderHolidaySettings" WHERE "ProviderId" = @p AND NOT "WorksOnHolidays" AND cardinality("DisabledHolidays") = 0""",
            Ct, ("p", provider))).Should().Be(1);
    }

    [Fact]
    public async Task AvailabilityMigration_LegacyRules_GetAVersionHeader()
    {
        // Arrange
        await using var old = await ScratchDatabase.CreateAsync(fixture, Ct);
        await using var db = await old.OpenAsync(Ct);
        foreach (var file in PreviousMigrations)
        {
            await RunMigrationAsync(db, file);
        }
        var provider = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand(
            """
            INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'Dra. Teste');
            INSERT INTO "AvailabilityRules" ("ProviderId","Weekday","StartsAtTime","EndsAtTime","EffectiveFrom","EffectiveTo")
            VALUES (@p, 1, '08:00', '12:00', '2026-01-05', '2026-06-30'), (@p, 2, '08:00', '12:00', '2026-01-05', '2026-06-30'),
                   (@p, 1, '09:00', '12:00', '2026-07-01', NULL);
            """, db))
        {
            seed.Parameters.AddWithValue("p", provider);
            await seed.ExecuteNonQueryAsync(Ct);
        }

        // Act
        await RunMigrationAsync(db, MigrationFile);
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        await using var check = new NpgsqlCommand(
            """
            SELECT string_agg(v."EffectiveFrom"::text || ':' || coalesce(v."EffectiveTo"::text, '-') || ':' ||
                   (SELECT count(*) FROM "AvailabilityRules" r WHERE r."VersionId" = v."Id")::text, ',' ORDER BY v."EffectiveFrom")
              FROM "AvailabilityVersions" v WHERE v."ProviderId" = @p
            """, db);
        check.Parameters.AddWithValue("p", provider);
        (await check.ExecuteScalarAsync(Ct) as string).Should().Be("2026-01-05:2026-06-30:2,2026-07-01:-:1");
    }

    [Fact]
    public async Task HolidaySettings_ForAnUnknownProvider_IsRejected()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);

        // Act
        var orphan = async () => await s.ExecuteAsync(
            """INSERT INTO "ProviderHolidaySettings" ("ProviderId") VALUES (gen_random_uuid())""", Ct);

        // Assert
        await orphan.Should().ThrowAsync<PostgresException>().WithMessage("*ProviderHolidaySettings_ProviderId_fkey*");
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
             WHERE table_schema = 'public' AND table_name IN ('ProviderHolidaySettings', 'AvailabilityRules', 'AvailabilityExceptions', 'AvailabilityVersions')
            UNION ALL
            SELECT conname || ' ' || pg_get_constraintdef(oid) FROM pg_constraint
             WHERE conrelid IN ('"ProviderHolidaySettings"'::regclass, '"AvailabilityRules"'::regclass, '"AvailabilityVersions"'::regclass,
                                '"AvailabilityExceptions"'::regclass, '"AuditEntries"'::regclass)
            UNION ALL
            SELECT indexdef FROM pg_indexes
             WHERE schemaname = 'public' AND tablename IN ('ProviderHolidaySettings', 'AvailabilityRules', 'AvailabilityExceptions', 'AvailabilityVersions')
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
