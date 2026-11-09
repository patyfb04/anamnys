using System.Security.Cryptography;
using System.Text.Json;
using Anamnys.RestoreCheck;
using Anamnys.Tests.Integrity;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static Anamnys.Tests.Integrity.IntegrityTestData;

namespace Anamnys.Tests.Backup;

// design/specs/2026-11-05-backup-restore-design.md §4.2 and §6: RestoreChecker on a scratch database and a fake object store.
[Collection(SharedAppHostCollection.Name)]
public class RestoreCheckTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] PdfBytes = "%PDF-1.7 conteudo"u8.ToArray();
    private static readonly byte[] ExternalBytes = "arquivo externo"u8.ToArray();

    private sealed record World(RetentionScratch Db, FakeNoteFileStore Store, FakeNoteFileStore Source, string Connection, Guid Provider, Guid Note);

    private async Task<World> HealthyAsync()
    {
        var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var note = await SignedNoteAsync(s, provider, patient, Now, Ct);
        await SignedDocumentAsync(s, provider, patient, Now.AddMinutes(1), Ct);
        var store = new FakeNoteFileStore();
        var source = new FakeNoteFileStore();

        store.Objects["notes/a.pdf"] = PdfBytes;
        await s.ExecuteAsync(
            """UPDATE "Notes" SET "Status" = 'Exported', "ExportedAt" = now(), "PdfObjectKey" = 'notes/a.pdf', "PdfSha256" = @h WHERE "Id" = @n""", Ct,
            ("h", Convert.ToHexStringLower(SHA256.HashData(PdfBytes))), ("n", note));

        store.Objects["external/b.bin"] = ExternalBytes;
        source.Objects["external/b.bin"] = ExternalBytes;
        await s.ExecuteAsync(
            """
            INSERT INTO "ExternalDocuments" ("PatientId","ProviderId","Filename","MimeType","ObjectKey","ByteSize","ScanStatus","Kind")
            VALUES (@pa, @p, 'x.pdf', 'application/pdf', 'external/b.bin', @size, 'clean', 'outro')
            """, Ct, ("pa", patient), ("p", provider), ("size", (long)ExternalBytes.Length));

        // The Keycloak database is the same scratch database: the check only needs the realm and user tables.
        await s.ExecuteAsync(
            """
            CREATE TABLE realm (id text PRIMARY KEY, name text NOT NULL);
            CREATE TABLE user_entity (id text PRIMARY KEY, realm_id text NOT NULL);
            INSERT INTO realm VALUES ('r1', 'anamnys-provider');
            INSERT INTO user_entity VALUES ('u1', 'r1'), ('u2', 'r1')
            """, Ct);

        await using var db = s.CreateDbContext();
        return new World(s, store, source, db.Database.GetConnectionString()!, provider, note);
    }

    private static RestoreChecker Checker(World w, bool withSource = false) =>
        new(w.Connection, w.Connection, w.Store, Tsa, withSource ? w.Source : null);

    private static CountSnapshot Snapshot(long notes, long users = 2) =>
        new(new Dictionary<string, long> { ["Providers"] = 1, ["Notes"] = notes }, new Dictionary<string, long> { ["anamnys-provider"] = users });

    private static CheckResult Check(RestoreCheckReport report, string name) => report.Checks.Single(c => c.Name == name);

    [Fact]
    public async Task RunAsync_HealthyDatabase_PassesEveryCheck()
    {
        // Arrange
        var world = await HealthyAsync();
        await using var _ = world.Db;

        // Act
        var report = await Checker(world, withSource: true).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        report.Passed.Should().BeTrue(string.Join(" | ", report.Checks.Select(c => $"{c.Name}: {c.Detail}")));
        report.Checks.Select(c => c.Name).Should().Equal("integrity", "row_counts", "files", "keycloak");
        Check(report, "files").Detail.Should().Contain("2 sampled");
    }

    [Fact]
    public async Task RunAsync_TamperedSeal_FailsIntegrity()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        await TamperSealsAsync(w.Db, """UPDATE "IntegritySeals" SET "Hash" = repeat('f', 64) WHERE "SubjectId" = @n""", Ct, ("n", w.Note));

        // Act
        var report = await Checker(w).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        report.Passed.Should().BeFalse();
        Check(report, "integrity").Passed.Should().BeFalse();
        Check(report, "integrity").Detail.Should().Contain("1 of 1 chains");
        Check(report, "files").Passed.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_CountBelowSnapshot_FailsRowCounts()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;

        // Act
        var report = await Checker(w).RunAsync(Snapshot(notes: 5), 200, Ct);

        // Assert
        report.Passed.Should().BeFalse();
        Check(report, "row_counts").Passed.Should().BeFalse();
        Check(report, "row_counts").Detail.Should().Contain("Notes: 1 < 5");
        Check(report, "integrity").Passed.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_PdfWithDifferentHash_FailsFiles()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        w.Store.Objects["notes/a.pdf"] = "%PDF-1.7 outro"u8.ToArray();

        // Act
        var report = await Checker(w).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        report.Passed.Should().BeFalse();
        Check(report, "files").Passed.Should().BeFalse();
        Check(report, "files").Detail.Should().Contain("1 PDF(s) with a different SHA-256");
    }

    [Fact]
    public async Task RunAsync_MissingObject_FailsFiles()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        w.Store.Objects.Remove("external/b.bin");

        // Act
        var report = await Checker(w).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        report.Passed.Should().BeFalse();
        Check(report, "files").Passed.Should().BeFalse();
        Check(report, "files").Detail.Should().Contain("1 missing object(s)");
    }

    [Fact]
    public async Task RunAsync_ExternalDocumentDifferentFromSource_FailsOnlyWithSource()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        var changed = (byte[])ExternalBytes.Clone();
        changed[0] ^= 0xFF;
        w.Source.Objects["external/b.bin"] = changed;

        // Act
        var without = await Checker(w).RunAsync(Snapshot(notes: 1), 200, Ct);
        var with = await Checker(w, withSource: true).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        without.Passed.Should().BeTrue();
        with.Passed.Should().BeFalse();
        Check(with, "files").Detail.Should().Contain("different from the source");
    }

    [Fact]
    public async Task RunAsync_KeycloakUsersBelowSnapshotOrNoRealm_FailsKeycloak()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;

        // Act
        var fewer = await Checker(w).RunAsync(Snapshot(notes: 1, users: 3), 200, Ct);
        await w.Db.ExecuteAsync("""DELETE FROM user_entity; DELETE FROM realm""", Ct);
        var none = await Checker(w).RunAsync(null, 200, Ct);

        // Assert
        Check(fewer, "keycloak").Passed.Should().BeFalse();
        Check(none, "keycloak").Detail.Should().Be("no anamnys realm found");
        Check(none, "row_counts").Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Render_Report_HasTheFixedShapeAndNoSecretsOrNames()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        var report = await Checker(w).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Act
        var json = JsonDocument.Parse(RestoreOutput.Render(report)).RootElement;

        // Assert
        json.EnumerateObject().Select(p => p.Name).Should().Equal("passed", "checks", "durationSeconds");
        json.GetProperty("checks").EnumerateArray().Should().OnlyContain(
            c => c.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "name", "passed", "detail" }));
        json.ToString().Should().NotContain("a.pdf").And.NotContain("x.pdf").And.NotContain("Ana");
    }

    [Fact]
    public async Task RunAsync_NoProviders_FailsIntegrity()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        var checker = new RestoreChecker(db.Database.GetConnectionString()!, db.Database.GetConnectionString()!, new FakeNoteFileStore(), Tsa);

        // Act
        var report = await checker.RunAsync(null, 200, Ct);

        // Assert
        Check(report, "integrity").Passed.Should().BeFalse();
        Check(report, "integrity").Detail.Should().Contain("no providers");
    }

    [Fact]
    public async Task RunAsync_NoSealsWhileSnapshotHasSeals_FailsIntegrity()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await s.AddProviderAsync(Ct, withCrp: true);
        await using var db = s.CreateDbContext();
        var checker = new RestoreChecker(db.Database.GetConnectionString()!, db.Database.GetConnectionString()!, new FakeNoteFileStore(), Tsa);
        var snapshot = new CountSnapshot(new Dictionary<string, long> { ["IntegritySeals"] = 3 }, null);

        // Act
        var withSnapshot = await checker.RunAsync(snapshot, 200, Ct);
        var without = await checker.RunAsync(null, 200, Ct);

        // Assert
        Check(withSnapshot, "integrity").Passed.Should().BeFalse();
        Check(withSnapshot, "integrity").Detail.Should().Contain("no seals");
        Check(without, "integrity").Passed.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_CandidatesButNothingChecked_FailsFiles()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;

        // Act
        var report = await Checker(w).RunAsync(Snapshot(notes: 1), 0, Ct);

        // Assert
        Check(report, "files").Passed.Should().BeFalse();
        Check(report, "files").Detail.Should().Contain("none was checked");
    }

    [Fact]
    public async Task RunAsync_NoFilesAtAll_PassesFilesWithNoFilesDetail()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        var checker = new RestoreChecker(db.Database.GetConnectionString()!, db.Database.GetConnectionString()!, new FakeNoteFileStore(), Tsa);

        // Act
        var report = await checker.RunAsync(null, 200, Ct);

        // Assert
        Check(report, "files").Passed.Should().BeTrue();
        Check(report, "files").Detail.Should().Be("nenhum arquivo para verificar");
    }

    [Fact]
    public async Task RunAsync_ExternalDocumentWithDifferentSize_FailsFiles()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        w.Store.Objects["external/b.bin"] = [.. ExternalBytes, 1];

        // Act
        var report = await Checker(w).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        Check(report, "files").Passed.Should().BeFalse();
        Check(report, "files").Detail.Should().Contain("1 external document(s) with a different size");
    }

    [Fact]
    public async Task RunAsync_NoSnapshot_ReportsRowCountsAsSkipped()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;

        // Act
        var report = await Checker(w).RunAsync(null, 200, Ct);

        // Assert
        Check(report, "row_counts").Should().Be(new CheckResult("row_counts", true, "skipped"));
    }

    [Fact]
    public async Task LoadAsync_CountsGivenButMissingValueUnreadableOrEmpty_Throws()
    {
        // Arrange
        var empty = Path.GetTempFileName();
        await File.WriteAllTextAsync(empty, "{}", Ct);
        await FluentActions.Awaiting(() => SnapshotFile.LoadAsync(CliOptions.Parse(["--counts"]))).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => SnapshotFile.LoadAsync(CliOptions.Parse(["--counts", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))])))
            .Should().ThrowAsync<IOException>();
        await FluentActions.Awaiting(() => SnapshotFile.LoadAsync(CliOptions.Parse(["--counts", empty])))
            .Should().ThrowAsync<InvalidDataException>();
        (await SnapshotFile.LoadAsync(CliOptions.Parse([]))).Should().BeNull();
        File.Delete(empty);
    }

    [Fact]
    public void Parse_ValuelessFlagAndEnvFallback_AreHandled()
    {
        // Arrange
        Environment.SetEnvironmentVariable("RESTORECHECK_S3_BUCKET", "from-env");
        try
        {
            // Act
            var options = CliOptions.Parse(["--counts", "--sample", "5", "--s3-endpoint", "http://x"]);

            // Assert
            options.Get("counts").Should().Be("");
            options.Get("sample").Should().Be("5");
            options.Get("s3-endpoint").Should().Be("http://x");
            options.Get("s3-bucket").Should().Be("from-env");
            options.Get("nothing-set").Should().BeNull();
            FluentActions.Invoking(() => options.Required("connection")).Should().Throw<ArgumentException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESTORECHECK_S3_BUCKET", null);
        }
    }

    private static RestoreChecker BaselineChecker(World w, string baseline) =>
        new(w.Connection, w.Connection, w.Store, Tsa, w.Source, baseline);

    [Fact]
    public async Task RunAsync_BaselineChainAlreadyBroken_IsReportedAsPreExistingAndPasses()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        await TamperSealsAsync(w.Db, """UPDATE "IntegritySeals" SET "Hash" = repeat('f', 64) WHERE "SubjectId" = @n""", Ct, ("n", w.Note));

        // Act: the baseline (here the same database) has the same problem.
        var report = await BaselineChecker(w, w.Connection).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        Check(report, "integrity").Passed.Should().BeTrue();
        Check(report, "integrity").Detail.Should().Contain("0 new; 1 pre-existing (");
    }

    [Fact]
    public async Task RunAsync_ChainBrokenOnlyInTheRestore_FailsWithBaseline()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        await using var healthy = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var healthyDb = healthy.CreateDbContext();
        await TamperSealsAsync(w.Db, """UPDATE "IntegritySeals" SET "Hash" = repeat('f', 64) WHERE "SubjectId" = @n""", Ct, ("n", w.Note));

        // Act: the baseline has no problem for this provider.
        var report = await BaselineChecker(w, healthyDb.Database.GetConnectionString()!).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        Check(report, "integrity").Passed.Should().BeFalse();
        Check(report, "integrity").Detail.Should().Contain("1 new; 0 pre-existing");
    }

    [Fact]
    public async Task RunAsync_ObjectMissingInRestoreButPresentInSource_FailsWithBaseline()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        w.Store.Objects.Remove("notes/a.pdf");
        w.Source.Objects["notes/a.pdf"] = PdfBytes;

        // Act
        var report = await BaselineChecker(w, w.Connection).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        Check(report, "files").Passed.Should().BeFalse();
        Check(report, "files").Detail.Should().Contain("1 missing object(s)").And.Contain("1 new; 0 pre-existing");
    }

    [Fact]
    public async Task RunAsync_ObjectMissingInRestoreAndSource_IsPreExistingWithBaseline()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        w.Store.Objects.Remove("notes/a.pdf");
        w.Store.Objects.Remove("external/b.bin");
        w.Source.Objects.Clear();

        // Act
        var report = await BaselineChecker(w, w.Connection).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        Check(report, "files").Passed.Should().BeTrue();
        Check(report, "files").Detail.Should().Contain("0 new; 2 pre-existing (2 missing object(s))");
    }

    [Fact]
    public async Task RunAsync_PdfWithDifferentHashInRestoreAndSourceCorrect_FailsWithBaseline()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        w.Store.Objects["notes/a.pdf"] = "%PDF-1.7 outro"u8.ToArray();
        w.Source.Objects["notes/a.pdf"] = PdfBytes;

        // Act
        var report = await BaselineChecker(w, w.Connection).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        Check(report, "files").Passed.Should().BeFalse();
        Check(report, "files").Detail.Should().Contain("1 PDF(s) with a different SHA-256");
    }

    [Fact]
    public async Task RunAsync_SourceUnreadableWithBaseline_FailsFilesInsteadOfPreExisting()
    {
        // Arrange: the restore lost an object and the source cannot be read (wrong endpoint, credentials, store down).
        var w = await HealthyAsync();
        await using var _ = w.Db;
        w.Store.Objects.Remove("notes/a.pdf");
        w.Source.FailGet = true;

        // Act
        var report = await BaselineChecker(w, w.Connection).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        Check(report, "files").Passed.Should().BeFalse();
        Check(report, "files").Detail.Should().Be("source store unreadable");
    }

    [Fact]
    public async Task RunAsync_BaselineDatabaseUnreadable_FailsIntegrity()
    {
        // Arrange
        var w = await HealthyAsync();
        await using var _ = w.Db;
        var missing = new Npgsql.NpgsqlConnectionStringBuilder(w.Connection) { Database = $"missing_{Guid.NewGuid():N}" }.ConnectionString;

        // Act
        var report = await BaselineChecker(w, missing).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        Check(report, "integrity").Passed.Should().BeFalse();
        Check(report, "integrity").Detail.Should().StartWith("baseline database unreadable");
    }

    [Fact]
    public async Task RunAsync_DifferentProblemOnChainBrokenLive_FailsWithBaseline()
    {
        // Arrange: live, the provider's chain has a sequence gap; the restore breaks it in another way.
        var w = await HealthyAsync();
        await using var _ = w.Db;
        await using var live = await RetentionScratch.CreateAsync(fixture, Ct);
        await live.ExecuteAsync(
            """
            INSERT INTO "Providers" ("Id","Email","ExternalSubject","Name","DocumentCheckMode")
            VALUES (@id, @id::text || '@test.local', gen_random_uuid(), 'Dra. Teste', 'blocking');
            INSERT INTO "IntegritySeals" ("ProviderId","Sequence","SubjectType","SubjectId","ContentHash","PreviousHash","Hash","SealedAt")
            VALUES (@id, 2, 'note', gen_random_uuid(), repeat('a', 64), repeat('c', 64), repeat('b', 64), now())
            """, Ct, ("id", w.Provider));
        await using var liveDb = live.CreateDbContext();
        await TamperSealsAsync(w.Db, """UPDATE "IntegritySeals" SET "Hash" = repeat('f', 64) WHERE "SubjectId" = @n""", Ct, ("n", w.Note));

        // Act
        var report = await BaselineChecker(w, liveDb.Database.GetConnectionString()!).RunAsync(Snapshot(notes: 1), 200, Ct);

        // Assert
        Check(report, "integrity").Passed.Should().BeFalse();
        Check(report, "integrity").Detail.Should().Contain("1 new; 0 pre-existing");
    }

    [Fact]
    public async Task RunAsync_OneRealmBelowSnapshotWhileTheSumHolds_FailsKeycloak()
    {
        // Arrange: anamnys-provider has 2 users (snapshot 1), anamnys-patient 1 (snapshot 2): the sum is equal.
        var w = await HealthyAsync();
        await using var _ = w.Db;
        await w.Db.ExecuteAsync(
            """INSERT INTO realm VALUES ('r2', 'anamnys-patient'); INSERT INTO user_entity VALUES ('u3', 'r2')""", Ct);
        var snapshot = new CountSnapshot(
            new Dictionary<string, long> { ["Providers"] = 1 },
            new Dictionary<string, long> { ["anamnys-provider"] = 1, ["anamnys-patient"] = 2 });

        // Act
        var report = await Checker(w).RunAsync(snapshot, 200, Ct);

        // Assert
        Check(report, "keycloak").Passed.Should().BeFalse();
        Check(report, "keycloak").Detail.Should().Be("users below the snapshot: anamnys-patient: 1 < 2");
    }
}
