using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.DocumentReading;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Storage;
using Anamnys.Tests.ExternalDocuments;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.DocumentReading;

[Collection(SharedAppHostCollection.Name)]
public class ReadWorkerTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class NoOcr : IOcrEngine
    {
        public Task<string> RecognizeAsync(byte[] image, CancellationToken ct) => Task.FromResult("");
    }

    private sealed class NoRender : IPageRenderer
    {
        public byte[] RenderPng(byte[] pdf, int pageIndex, int dpi) => [];
    }

    private sealed class ScriptedReader(params Func<DocumentReadResult>[] script) : IDocumentReader
    {
        public int Calls { get; private set; }

        public Task<DocumentReadResult> ReadAsync(byte[] content, string mimeType, CancellationToken ct)
        {
            var step = script[Math.Min(Calls, script.Length - 1)];
            Calls++;
            return Task.FromResult(step());
        }
    }

    private static IDocumentReader RealReader(int maxPages = 20) =>
        new DocumentReader(new NoRender(), new NoOcr(), Options.Create(new DocumentReadingOptions { MaxPages = maxPages }));

    private static ServiceProvider ServicesFor(PatientSearchSeed seed, FakeNoteFileStore store, IDocumentReader reader)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => seed.CreateDbContext());
        services.AddSingleton<INoteFileStore>(store);
        services.AddSingleton(reader);
        return services.BuildServiceProvider();
    }

    private static Task<(ReadStep Step, TimeSpan Delay)> RunAsync(ServiceProvider services, Guid documentId, Guid providerId) =>
        ReadWorker.ProcessAsync(
            new ReadJob(documentId, providerId), services.GetRequiredService<IServiceScopeFactory>(),
            new FakeTimeProvider(Now), NullLogger.Instance, Ct);

    private static async Task<ExternalDocument> AddCleanAsync(
        AnamnysDbContext db, FakeNoteFileStore store, Guid providerId, Guid patientId, string? readStatus = "pending",
        string sample = "vinicius-andrade-relatorio-psiquiatrico.pdf", DateTimeOffset? deletedAt = null)
    {
        var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, providerId, patientId, "clean", deletedAt: deletedAt);
        store.Objects[document.ObjectKey!] = TestPdfs.Sample(sample);
        await db.ExternalDocuments.Where(d => d.Id == document.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReadStatus, readStatus), Ct);
        return document;
    }

    private static Func<DocumentReadResult> Broken => () => throw new InvalidDataException("bad xref");

    [Fact]
    public async Task Process_PendingPdf_StoresEveryPageAndMarksTheDocumentRead()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await AddCleanAsync(db, store, seed.ProviderId, patient);
        await using var services = ServicesFor(seed, store, RealReader());

        // Act
        var result = await RunAsync(services, document.Id, seed.ProviderId);

        // Assert
        result.Step.Should().Be(ReadStep.Done);
        var row = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        row.ReadStatus.Should().Be("done");
        row.ReadAt.Should().Be(Now);
        row.PageCount.Should().Be(2);
        row.PagesTruncated.Should().BeFalse();
        var pages = await db.ExternalDocumentPages.AsNoTracking()
            .Where(p => p.ExternalDocumentId == document.Id).OrderBy(p => p.PageNumber).ToListAsync(Ct);
        pages.Select(p => p.PageNumber).Should().Equal(1, 2);
        pages.Should().OnlyContain(p => p.ProviderId == seed.ProviderId && !p.FromOcr && p.CreatedAt == Now);
        pages[0].Text.Should().Contain("CID-10 F33.1");
        pages[1].Text.Should().Contain("Bupropiona 150 mg");
    }

    [Fact]
    public async Task Process_MorePagesThanTheLimit_StoresTheFirstOnes_AndFlagsTheDocument()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await AddCleanAsync(db, store, seed.ProviderId, patient);
        await using var services = ServicesFor(seed, store, RealReader(maxPages: 1));

        // Act
        await RunAsync(services, document.Id, seed.ProviderId);

        // Assert
        var row = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        row.PageCount.Should().Be(2);
        row.PagesTruncated.Should().BeTrue();
        (await db.ExternalDocumentPages.CountAsync(p => p.ExternalDocumentId == document.Id, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Process_ReadAgain_ReplacesThePagesOfTheEarlierRead()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await AddCleanAsync(db, store, seed.ProviderId, patient);
        await using var services = ServicesFor(seed, store, new ScriptedReader(
            () => new DocumentReadResult(2, false, [new ReadPage(1, "primeira leitura", false), new ReadPage(2, "segunda página", false)]),
            () => new DocumentReadResult(1, false, [new ReadPage(1, "leitura nova", true)])));
        await RunAsync(services, document.Id, seed.ProviderId);
        await db.ExternalDocuments.Where(d => d.Id == document.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReadStatus, "pending"), Ct);

        // Act
        await RunAsync(services, document.Id, seed.ProviderId);

        // Assert
        var pages = await db.ExternalDocumentPages.AsNoTracking().Where(p => p.ExternalDocumentId == document.Id).ToListAsync(Ct);
        pages.Should().ContainSingle().Which.Should().Match<ExternalDocumentPage>(p => p.Text == "leitura nova" && p.FromOcr);
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct)).PageCount.Should().Be(1);
    }

    [Fact]
    public async Task Process_ReaderErrors_RetryTwiceThenFailTheRead()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await AddCleanAsync(db, store, seed.ProviderId, patient);
        var reader = new ScriptedReader(Broken);
        await using var services = ServicesFor(seed, store, reader);

        // Act
        var first = await RunAsync(services, document.Id, seed.ProviderId);
        var afterFirst = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        var second = await RunAsync(services, document.Id, seed.ProviderId);
        var third = await RunAsync(services, document.Id, seed.ProviderId);
        var fourth = await RunAsync(services, document.Id, seed.ProviderId);

        // Assert
        first.Step.Should().Be(ReadStep.Retry);
        afterFirst.ReadStatus.Should().Be("pending");
        afterFirst.ReadAttempts.Should().Be(1);
        second.Step.Should().Be(ReadStep.Retry);
        second.Delay.Should().BeGreaterThan(first.Delay);
        third.Step.Should().Be(ReadStep.Done);
        var row = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        row.ReadStatus.Should().Be("failed");
        row.ReadAttempts.Should().Be(3);
        row.ReadAt.Should().Be(Now);
        fourth.Step.Should().Be(ReadStep.Done);
        reader.Calls.Should().Be(3);
        (await db.ExternalDocumentPages.CountAsync(p => p.ExternalDocumentId == document.Id, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Process_MissingObjectUnsupportedTypeOrMalformedPdf_FailsTheReadAtOnce()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var missing = await AddCleanAsync(db, store, seed.ProviderId, patient);
        store.Objects.Remove(missing.ObjectKey!);
        var unsupported = await AddCleanAsync(db, store, seed.ProviderId, patient);
        await db.ExternalDocuments.Where(d => d.Id == unsupported.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.MimeType, "image/gif"), Ct);
        var malformed = await AddCleanAsync(db, store, seed.ProviderId, patient);
        store.Objects[malformed.ObjectKey!] = "%PDF-1.4 but nothing else"u8.ToArray();
        await using var services = ServicesFor(seed, store, RealReader());

        // Act
        var a = await RunAsync(services, missing.Id, seed.ProviderId);
        var b = await RunAsync(services, unsupported.Id, seed.ProviderId);
        var c = await RunAsync(services, malformed.Id, seed.ProviderId);

        // Assert
        new[] { a.Step, b.Step, c.Step }.Should().OnlyContain(s => s == ReadStep.Done);
        var rows = await db.ExternalDocuments.AsNoTracking()
            .Where(d => d.Id == missing.Id || d.Id == unsupported.Id || d.Id == malformed.Id).ToListAsync(Ct);
        rows.Should().HaveCount(3).And.OnlyContain(d => d.ReadStatus == "failed" && d.ReadAttempts == 1 && d.ReadAt == Now);
    }

    [Fact]
    public async Task Process_CountsTheAttemptBeforeReading_SoAReadThatNeverReturnsStillCounts()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await AddCleanAsync(db, store, seed.ProviderId, patient);
        (string? Status, int Attempts) during = default;
        var reader = new ScriptedReader(() =>
        {
            using var probe = seed.CreateDbContext();
            var row = probe.ExternalDocuments.AsNoTracking().Single(d => d.Id == document.Id);
            during = (row.ReadStatus, row.ReadAttempts);
            return new DocumentReadResult(1, false, [new ReadPage(1, "texto", false)]);
        });
        await using var services = ServicesFor(seed, store, reader);

        // Act
        await RunAsync(services, document.Id, seed.ProviderId);

        // Assert
        during.Should().Be(("reading", 1));
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct)).ReadAttempts.Should().Be(1);
    }

    [Fact]
    public async Task PoisonDocument_ReadingWithAttemptsUsedUp_IsFailedByTheSweepAndByProcess_WithoutReading()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var swept = await AddCleanAsync(db, store, seed.ProviderId, patient, readStatus: "reading");
        var processed = await AddCleanAsync(db, store, seed.ProviderId, patient, readStatus: "reading");
        var retried = await AddCleanAsync(db, store, seed.ProviderId, patient, readStatus: "reading");
        await db.ExternalDocuments.Where(d => d.Id == swept.Id || d.Id == processed.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReadAttempts, ReadWorker.MaxAttempts), Ct);
        await db.ExternalDocuments.Where(d => d.Id == retried.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ReadAttempts, ReadWorker.MaxAttempts - 1), Ct);
        var reader = new ScriptedReader(Broken);
        await using var services = ServicesFor(seed, store, reader);
        var queue = new ReadQueue();

        // Act
        var direct = await RunAsync(services, processed.Id, seed.ProviderId);
        await ReadWorker.SweepAsync(services.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger.Instance, Ct, new FakeTimeProvider(Now));
        var queued = new List<ReadJob>();
        while (queue.Reader.TryRead(out var job))
        {
            queued.Add(job);
        }
        var last = await RunAsync(services, retried.Id, seed.ProviderId);

        // Assert
        direct.Step.Should().Be(ReadStep.Done);
        reader.Calls.Should().Be(1);
        queued.Where(j => j.ProviderId == seed.ProviderId).Select(j => j.DocumentId).Should().Equal(retried.Id);
        last.Step.Should().Be(ReadStep.Done);
        var rows = await db.ExternalDocuments.AsNoTracking()
            .Where(d => d.Id == swept.Id || d.Id == processed.Id || d.Id == retried.Id).ToListAsync(Ct);
        rows.Should().HaveCount(3).And.OnlyContain(d => d.ReadStatus == "failed" && d.ReadAttempts == ReadWorker.MaxAttempts && d.ReadAt == Now);
    }

    [Fact]
    public async Task Process_DocumentDeletedWhileTheReadIsSaved_WaitsForTheDeletion_AndStoresNoPages()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await AddCleanAsync(db, store, seed.ProviderId, patient);
        await using var deleter = seed.CreateDbContext();
        Task? commit = null;
        var reader = new DeletingReader(async () =>
        {
            // A deletion holds the row (uncommitted) while the read finishes, and commits a moment later.
            var connection = deleter.Database.GetDbConnection();
            await connection.OpenAsync(Ct);
            var tx = await connection.BeginTransactionAsync(Ct);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = """UPDATE "ExternalDocuments" SET "DeletedAt" = @now, "ObjectKey" = NULL WHERE "Id" = @id""";
                command.Parameters.Add(new Npgsql.NpgsqlParameter("now", Now));
                command.Parameters.Add(new Npgsql.NpgsqlParameter("id", document.Id));
                await command.ExecuteNonQueryAsync(Ct);
            }
            commit = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
                await tx.CommitAsync(Ct);
                await tx.DisposeAsync();
            }, Ct);
        });
        await using var services = ServicesFor(seed, store, reader);

        // Act
        var result = await RunAsync(services, document.Id, seed.ProviderId);
        await commit!;

        // Assert
        result.Step.Should().Be(ReadStep.Done);
        (await db.ExternalDocumentPages.CountAsync(p => p.ExternalDocumentId == document.Id, Ct)).Should().Be(0);
        var row = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        row.DeletedAt.Should().Be(Now);
        row.ReadStatus.Should().NotBe("done");
    }

    private sealed class DeletingReader(Func<Task> duringRead) : IDocumentReader
    {
        public async Task<DocumentReadResult> ReadAsync(byte[] content, string mimeType, CancellationToken ct)
        {
            await duringRead();
            return new DocumentReadResult(1, false, [new ReadPage(1, "texto lido", false)]);
        }
    }

    [Fact]
    public async Task Process_DoneDeletedUnreadOrAnotherProvidersJob_IsSkipped()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var done = await AddCleanAsync(db, store, seed.ProviderId, patient, readStatus: "done");
        var deleted = await AddCleanAsync(db, store, seed.ProviderId, patient, deletedAt: Now);
        var unread = await AddCleanAsync(db, store, seed.ProviderId, patient, readStatus: null);
        var mine = await AddCleanAsync(db, store, seed.ProviderId, patient);
        var reader = new ScriptedReader(Broken);
        await using var services = ServicesFor(seed, store, reader);

        // Act
        await RunAsync(services, done.Id, seed.ProviderId);
        await RunAsync(services, deleted.Id, seed.ProviderId);
        await RunAsync(services, unread.Id, seed.ProviderId);
        await RunAsync(services, mine.Id, other);

        // Assert
        reader.Calls.Should().Be(0);
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == mine.Id, Ct)).ReadStatus.Should().Be("pending");
    }

    [Fact]
    public async Task Sweep_RequeuesPendingAndInterruptedReadsOnly()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var pending = await AddCleanAsync(db, store, seed.ProviderId, patient);
        var reading = await AddCleanAsync(db, store, seed.ProviderId, patient, readStatus: "reading");
        await AddCleanAsync(db, store, seed.ProviderId, patient, readStatus: "done");
        await AddCleanAsync(db, store, seed.ProviderId, patient, readStatus: "failed");
        await AddCleanAsync(db, store, seed.ProviderId, patient, readStatus: null);
        await AddCleanAsync(db, store, seed.ProviderId, patient, deletedAt: Now);
        await using var services = ServicesFor(seed, store, RealReader());
        var queue = new ReadQueue();

        // Act
        await ReadWorker.SweepAsync(services.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger.Instance, Ct);

        // Assert
        var queued = new List<ReadJob>();
        while (queue.Reader.TryRead(out var job))
        {
            queued.Add(job);
        }
        // The sweep reads every provider's rows (the dev database is shared): only ours matter.
        queued.Where(j => j.ProviderId == seed.ProviderId).Select(j => j.DocumentId).Should().BeEquivalentTo([pending.Id, reading.Id]);
    }

    [Fact]
    public async Task ScanThenRead_CleanVerdict_QueuesTheReadAndTheWorkerStoresThePages()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending");
        store.Objects[document.ObjectKey!] = TestPdfs.Sample("fernanda-barbosa-receita.pdf");
        var queue = new ReadQueue();
        var services = new ServiceCollection();
        services.AddScoped(_ => seed.CreateDbContext());
        services.AddSingleton<INoteFileStore>(store);
        services.AddSingleton<IVirusScanner>(new FakeVirusScanner());
        services.AddSingleton(queue);
        services.AddSingleton(RealReader());
        await using var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();

        // Act
        await ScanWorker.ProcessAsync(new ScanJob(document.Id, seed.ProviderId), scopes, new FakeTimeProvider(Now), NullLogger.Instance, Ct);
        var afterScan = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        queue.Reader.TryRead(out var job).Should().BeTrue();
        await ReadWorker.ProcessAsync(job!, scopes, new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        afterScan.ScanStatus.Should().Be("clean");
        afterScan.ReadStatus.Should().Be("pending");
        job.Should().Be(new ReadJob(document.Id, seed.ProviderId));
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct)).ReadStatus.Should().Be("done");
        (await db.ExternalDocumentPages.AsNoTracking().SingleAsync(p => p.ExternalDocumentId == document.Id, Ct)).Text
            .Should().Contain("Sertralina 50 mg");
    }

    [Fact]
    public async Task DeleteAsync_ReadDocument_RemovesItsPages()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await AddCleanAsync(db, store, seed.ProviderId, patient);
        await using var services = ServicesFor(seed, store, RealReader());
        await RunAsync(services, document.Id, seed.ProviderId);

        // Act
        var outcome = await ExternalDocumentService.DeleteAsync(db, store, seed.ProviderId, document.Id, Now, Ct);

        // Assert
        outcome.Should().Be(ExternalDocumentOutcome.Ok);
        (await db.ExternalDocumentPages.CountAsync(p => p.ExternalDocumentId == document.Id, Ct)).Should().Be(0);
    }
}
