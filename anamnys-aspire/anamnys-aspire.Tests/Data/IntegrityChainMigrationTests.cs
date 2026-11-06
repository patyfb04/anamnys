using Anamnys.Tests.Retention;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

// design/migrations/2026-11-03-integrity-chain.sql against the previous schema and the bootstrap script, and the
// append-only triggers (design/specs/2026-11-03-integrity-chain-design.md, section 2).
[Collection(SharedAppHostCollection.Name)]
public class IntegrityChainMigrationTests(SharedAppHostFixture fixture)
{
    private const string MigrationFile = "2026-11-03-integrity-chain.sql";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PreviousMigrations =
    [
        "2026-10-03-patient-accounts.sql", "2026-10-04-patient-invitations.sql", "2026-10-07-appointment-notifications.sql",
        "2026-10-10-live-session.sql", "2026-10-12-note-structuring.sql", "2026-10-13-note-review.sql", "2026-10-17-note-pdf.sql",
        "2026-10-18-scales.sql", "2026-10-19-documents.sql", "2026-10-20-documents-team.sql", "2026-10-21-theme-vocabulary.sql",
        "2026-10-23-account-export.sql", "2026-10-24-excerpt-refs.sql", "2026-10-25-external-documents.sql", "2026-10-26-document-facts.sql",
        "2026-10-27-medication-timeline.sql", "2026-10-28-retention-panel.sql", "2026-10-29-theme-absence.sql", "2026-10-30-plan-adherence.sql",
        "2026-10-30-patient-quotes.sql", "2026-10-31-record-search.sql", "2026-11-01-protected-sharing.sql",
    ];

    [Fact]
    public async Task IntegrityChainMigration_FromPreviousSchema_RunTwice_AgreesWithTheBootstrapSchema()
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
            await RunMigrationAsync(db, "2026-11-04-patient-dossier.sql"); // the bootstrap script already includes it
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
        freshShape.Should().Contain(s => s.StartsWith("IntegritySeals_Previous_ck"));
        freshShape.Should().Contain(s => s.StartsWith("IntegritySeals_Subject_key") && s.Contains("UNIQUE"));
        freshShape.Should().Contain(s => s.StartsWith("IntegrityAnchors_Provider_Sequence_key"));
        freshShape.Should().Contain(s => s.StartsWith("trigger IntegritySeals_append_only"));
        freshShape.Should().Contain(s => s.StartsWith("trigger IntegrityAnchors_append_only"));
        freshShape.Should().Contain(s => s.StartsWith("AuditEntries_subject_ck") && s.Contains("integrity_report_downloaded") && s.Contains("integrity_check_failed"));
        freshShape.Should().NotContain(s => s.Contains("DocumentHashes"));
    }

    [Fact]
    public async Task DocumentHashes_IsGone()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);

        // Act
        var count = await s.CountAsync("""SELECT count(*) FROM information_schema.tables WHERE table_name = 'DocumentHashes'""", Ct);

        // Assert
        count.Should().Be(0);
    }

    private static async Task<Guid> AddSealAsync(RetentionScratch s, Guid provider, long sequence, string? previous = null)
    {
        var id = Guid.NewGuid();
        await s.ExecuteAsync(
            """
            INSERT INTO "IntegritySeals" ("Id","ProviderId","Sequence","SubjectType","SubjectId","ContentHash","PreviousHash","Hash","SealedAt")
            VALUES (@id, @p, @seq, 'note', gen_random_uuid(), repeat('a', 64), @prev, repeat('b', 64), now())
            """, Ct, ("id", id), ("p", provider), ("seq", sequence), ("prev", (object?)previous ?? DBNull.Value));
        return id;
    }

    [Fact]
    public async Task Seal_UpdateOfAnythingButDiscardedAt_Fails()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var id = await AddSealAsync(s, provider, 1);

        // Act
        var hash = async () => await s.ExecuteAsync("""UPDATE "IntegritySeals" SET "Hash" = repeat('c', 64) WHERE "Id" = @id""", Ct, ("id", id));
        var both = async () => await s.ExecuteAsync(
            """UPDATE "IntegritySeals" SET "Hash" = repeat('c', 64), "DiscardedAt" = now() WHERE "Id" = @id""", Ct, ("id", id));
        var retro = async () => await s.ExecuteAsync("""UPDATE "IntegritySeals" SET "Retroactive" = true WHERE "Id" = @id""", Ct, ("id", id));

        // Assert
        await hash.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        await both.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        await retro.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
    }

    [Fact]
    public async Task Seal_Delete_Fails()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var id = await AddSealAsync(s, provider, 1);

        // Act
        var delete = async () => await s.ExecuteAsync("""DELETE FROM "IntegritySeals" WHERE "Id" = @id""", Ct, ("id", id));

        // Assert
        await delete.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        (await s.CountAsync("""SELECT count(*) FROM "IntegritySeals" WHERE "Id" = @id""", Ct, ("id", id))).Should().Be(1);
    }

    [Fact]
    public async Task Seal_DiscardedAt_PassesOnce_ThenFailsAgain()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var id = await AddSealAsync(s, provider, 1);

        // Act
        await s.ExecuteAsync("""UPDATE "IntegritySeals" SET "DiscardedAt" = now() WHERE "Id" = @id""", Ct, ("id", id));
        var again = async () => await s.ExecuteAsync("""UPDATE "IntegritySeals" SET "DiscardedAt" = now() + interval '1 day' WHERE "Id" = @id""", Ct, ("id", id));
        var clear = async () => await s.ExecuteAsync("""UPDATE "IntegritySeals" SET "DiscardedAt" = NULL WHERE "Id" = @id""", Ct, ("id", id));

        // Assert
        (await s.CountAsync("""SELECT count(*) FROM "IntegritySeals" WHERE "Id" = @id AND "DiscardedAt" IS NOT NULL""", Ct, ("id", id))).Should().Be(1);
        await again.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        await clear.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
    }

    [Fact]
    public async Task Seal_ChainShapeConstraints()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        await AddSealAsync(s, provider, 1);

        // Act
        var firstWithPrevious = async () => await AddSealAsync(s, provider, 2, previous: null);
        var duplicateSequence = async () => await AddSealAsync(s, provider, 1);
        var badType = async () => await s.ExecuteAsync(
            """
            INSERT INTO "IntegritySeals" ("ProviderId","Sequence","SubjectType","SubjectId","ContentHash","Hash","SealedAt")
            VALUES (@p, 1, 'other', gen_random_uuid(), 'a', 'b', now())
            """, Ct, ("p", provider));

        // Assert
        await firstWithPrevious.Should().ThrowAsync<PostgresException>().WithMessage("*IntegritySeals_Previous_ck*");
        await duplicateSequence.Should().ThrowAsync<PostgresException>().WithMessage("*IntegritySeals_Provider_Sequence_key*");
        await badType.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task Anchor_UpdateAndDelete_Fail()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        await s.ExecuteAsync(
            """
            INSERT INTO "IntegrityAnchors" ("ProviderId","Sequence","HeadHash","TsaUrl","Token","AnchoredAt")
            VALUES (@p, 1, repeat('a', 64), 'https://tsa.test', '\x0102'::bytea, now())
            """, Ct, ("p", provider));

        // Act
        var update = async () => await s.ExecuteAsync("""UPDATE "IntegrityAnchors" SET "TsaUrl" = 'x' WHERE "ProviderId" = @p""", Ct, ("p", provider));
        var delete = async () => await s.ExecuteAsync("""DELETE FROM "IntegrityAnchors" WHERE "ProviderId" = @p""", Ct, ("p", provider));

        // Assert
        await update.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        await delete.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        (await s.CountAsync("""SELECT count(*) FROM "IntegrityAnchors" WHERE "ProviderId" = @p""", Ct, ("p", provider))).Should().Be(1);
    }

    [Fact]
    public async Task SealsAndAnchors_Truncate_Fails()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        await AddSealAsync(s, provider, 1);

        // Act
        var seals = async () => await s.ExecuteAsync("TRUNCATE TABLE \"IntegritySeals\"", Ct);
        var anchors = async () => await s.ExecuteAsync("TRUNCATE TABLE \"IntegrityAnchors\"", Ct);

        // Assert
        await seals.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        await anchors.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");
        (await s.CountAsync("SELECT count(*) FROM \"IntegritySeals\"", Ct)).Should().Be(1);
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
             WHERE table_schema = 'public' AND table_name IN ('IntegritySeals', 'IntegrityAnchors', 'DocumentHashes')
            UNION ALL
            SELECT conname || ' ' || pg_get_constraintdef(oid) FROM pg_constraint
             WHERE conrelid IN ('"IntegritySeals"'::regclass, '"IntegrityAnchors"'::regclass, '"AuditEntries"'::regclass)
            UNION ALL
            SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('IntegritySeals', 'IntegrityAnchors')
            UNION ALL
            SELECT 'trigger ' || tgname || ' ' || tgrelid::regclass::text || ' ' || tgtype FROM pg_trigger
             WHERE tgrelid IN ('"IntegritySeals"'::regclass, '"IntegrityAnchors"'::regclass) AND NOT tgisinternal
            UNION ALL
            SELECT 'function ' || p.proname || ' ' || md5(p.prosrc) FROM pg_proc p
             WHERE p.proname IN ('IntegritySeals_append_only', 'IntegrityAnchors_append_only', 'Integrity_no_truncate')
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
