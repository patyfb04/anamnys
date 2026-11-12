using Anamnys.Tests.Retention;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-11-10-appointment-series.sql against the previous schema and the bootstrap script, plus the
// new constraints (design/specs/2026-11-09-appointment-series-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class AppointmentSeriesMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-11-10-appointment-series.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
        "2026-10-27-medication-timeline.sql", "2026-10-28-retention-panel.sql", "2026-10-29-theme-absence.sql", "2026-10-30-plan-adherence.sql",
        "2026-10-30-patient-quotes.sql", "2026-10-31-record-search.sql", "2026-11-01-protected-sharing.sql", "2026-11-03-integrity-chain.sql",
        "2026-11-04-patient-dossier.sql", "2026-11-05-backup-runs.sql", "2026-11-09-availability.sql",
    ];

    [Fact]
    public async Task SeriesMigration_FromPreviousSchema_RunTwice_AgreesWithTheBootstrapSchema()
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

        // Assert
        oldShape.Should().BeEquivalentTo(freshShape);
        freshShape.Should().Contain("AppointmentSeries.Interval integer NO 1");
        freshShape.Should().Contain("AppointmentSeries.Weekday integer NO ");
        freshShape.Should().Contain("AppointmentSeries.StartTime time without time zone NO ");
        freshShape.Should().Contain("AppointmentSeries.Timezone text NO 'America/Sao_Paulo'::text");
        freshShape.Should().Contain("AppointmentSeries.OccurrenceCount integer YES ");
        freshShape.Should().Contain("AppointmentSeries.GeneratedUntil date YES ");
        freshShape.Should().Contain("AppointmentSeries.PreviousSeriesId uuid YES ");
        freshShape.Should().Contain("AppointmentSeries.CreatedAt timestamp with time zone NO now()");
        freshShape.Should().Contain("Appointments.SeriesIndex integer YES ");
        freshShape.Should().Contain("Appointments.Detached boolean NO false");
        freshShape.Should().Contain(s => s.StartsWith("AppointmentSeries_Interval_ck") && s.Contains("1") && s.Contains("2"));
        freshShape.Should().Contain(s => s.StartsWith("AppointmentSeries_Weekday_ck") && s.Contains("6"));
        freshShape.Should().Contain(s => s.StartsWith("AppointmentSeries_OccurrenceCount_ck") && s.Contains("520"));
        freshShape.Should().Contain(s => s.StartsWith("AppointmentSeries_End_ck"));
        freshShape.Should().Contain(s => s.StartsWith("AppointmentSeries_Range_ck"));
        freshShape.Should().Contain(s => s.StartsWith("AppointmentSeries_ProviderId_fkey") && s.Contains("Providers"));
        freshShape.Should().Contain(s => s.StartsWith("AppointmentSeries_Patient_fk") && s.Contains("Patients"));
        freshShape.Should().Contain(s => s.StartsWith("AppointmentSeries_PreviousSeriesId_fkey") && s.Contains("SET NULL"));
        freshShape.Should().Contain(s => s.Contains("Appointments_Series_Index_key") && s.Contains("UNIQUE") && s.Contains("\"SeriesIndex\""));
        freshShape.Should().Contain(s => s.Contains("Appointments_Series_StartsAt_idx") && s.Contains("\"StartsAt\""));
        freshShape.Should().Contain(s => s.StartsWith("AuditEntries_subject_ck") && s.Contains("series_created")
            && s.Contains("series_split") && s.Contains("series_ended") && s.Contains("appointment_outside_availability"));
    }

    [Fact]
    public async Task Series_RejectsBadPatternsAndDuplicateIndexes_AndAuditAcceptsTheNewActions()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        const string insert = """
            INSERT INTO "AppointmentSeries" ("Id","PatientId","ProviderId","RecurrenceRule","StartsOn","EndsOn","OccurrenceCount","Interval","Weekday","StartTime")
            VALUES (@id, @pa, @p, 'WEEKLY;INTERVAL=1;BYDAY=TU', '2026-11-17', @ends::date, @count, @interval, 2, '14:00')
            """;
        Task Insert(Guid id, object ends, object count, int interval) =>
            s.ExecuteAsync(insert, Ct, ("id", id), ("pa", patient), ("p", provider), ("ends", ends), ("count", count), ("interval", interval));

        // Act
        var both = async () => await Insert(Guid.NewGuid(), "2026-12-01", 3, 1);
        var backwards = async () => await Insert(Guid.NewGuid(), "2026-11-16", DBNull.Value, 1);
        var interval = async () => await Insert(Guid.NewGuid(), DBNull.Value, DBNull.Value, 3);
        var tooMany = async () => await Insert(Guid.NewGuid(), DBNull.Value, 521, 1);
        var series = Guid.NewGuid();
        await Insert(series, DBNull.Value, 10, 2);
        const string occurrence = """
            INSERT INTO "Appointments" ("ProviderId","PatientId","SeriesId","SeriesIndex","StartsAt","EndsAt")
            VALUES (@p, @pa, @s, 1, @at, @at + interval '50 minutes')
            """;
        await s.ExecuteAsync(occurrence, Ct, ("p", provider), ("pa", patient), ("s", series), ("at", new DateTimeOffset(2026, 11, 17, 17, 0, 0, TimeSpan.Zero)));
        var duplicate = async () => await s.ExecuteAsync(occurrence, Ct,
            ("p", provider), ("pa", patient), ("s", series), ("at", new DateTimeOffset(2026, 11, 24, 17, 0, 0, TimeSpan.Zero)));
        foreach (var action in new[] { "series_created", "series_split", "series_ended" })
        {
            await s.ExecuteAsync(
                """INSERT INTO "AuditEntries" ("ActorId","Action","FieldChanged","NewValue","At") VALUES (@p, @a, 'series', 'x', now())""",
                Ct, ("p", provider), ("a", action));
        }

        // Assert
        await both.Should().ThrowAsync<PostgresException>().WithMessage("*AppointmentSeries_End_ck*");
        await backwards.Should().ThrowAsync<PostgresException>().WithMessage("*AppointmentSeries_Range_ck*");
        await interval.Should().ThrowAsync<PostgresException>().WithMessage("*AppointmentSeries_Interval_ck*");
        await tooMany.Should().ThrowAsync<PostgresException>().WithMessage("*AppointmentSeries_OccurrenceCount_ck*");
        await duplicate.Should().ThrowAsync<PostgresException>().WithMessage("*Appointments_Series_Index_key*");
        (await s.CountAsync(
            """SELECT count(*) FROM "AppointmentSeries" WHERE "ProviderId" = @p AND "Timezone" = 'America/Sao_Paulo' AND "CancelledAt" IS NULL""",
            Ct, ("p", provider))).Should().Be(1);
        (await s.CountAsync("""SELECT count(*) FROM "Appointments" WHERE "SeriesId" = @s AND NOT "Detached" """, Ct, ("s", series))).Should().Be(1);
        (await s.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "ActorId" = @p""", Ct, ("p", provider))).Should().Be(3);
    }

    [Fact]
    public async Task SeriesMigration_LegacySeries_TakeTheirWeekdayFromTheStartDate()
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
        await using (var seed = new NpgsqlCommand(
            """
            INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name") VALUES (@p, @p::text || '@test.local', gen_random_uuid(), 'Dra. Teste');
            INSERT INTO "Patients" ("Id","ProviderId","FirstName","LastName") VALUES (@pa, @p, 'Ana', 'Silva');
            INSERT INTO "AppointmentSeries" ("PatientId","ProviderId","RecurrenceRule","StartsOn") VALUES (@pa, @p, 'FREQ=WEEKLY', '2026-11-19');
            """, db))
        {
            seed.Parameters.AddWithValue("p", provider);
            seed.Parameters.AddWithValue("pa", patient);
            await seed.ExecuteNonQueryAsync(Ct);
        }

        // Act
        await RunMigrationAsync(db, MigrationFile);
        await RunMigrationAsync(db, MigrationFile);

        // Assert
        await using var check = new NpgsqlCommand(
            """SELECT "Weekday"::text || ' ' || "StartTime"::text || ' ' || "Interval"::text FROM "AppointmentSeries" WHERE "ProviderId" = @p""", db);
        check.Parameters.AddWithValue("p", provider);
        (await check.ExecuteScalarAsync(Ct) as string).Should().Be("4 00:00:00 1");
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
             WHERE table_schema = 'public' AND table_name IN ('AppointmentSeries', 'Appointments')
            UNION ALL
            SELECT conname || ' ' || pg_get_constraintdef(oid) FROM pg_constraint
             WHERE conrelid IN ('"AppointmentSeries"'::regclass, '"Appointments"'::regclass, '"AuditEntries"'::regclass)
            UNION ALL
            SELECT indexdef FROM pg_indexes
             WHERE schemaname = 'public' AND tablename IN ('AppointmentSeries', 'Appointments')
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
