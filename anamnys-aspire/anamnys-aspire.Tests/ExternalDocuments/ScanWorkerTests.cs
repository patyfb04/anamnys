using Anamnys.Server.Data;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Storage;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.ExternalDocuments;

[Collection(SharedAppHostCollection.Name)]
public class ScanWorkerTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScriptedScanner(params Func<ScanVerdict>[] script) : IVirusScanner
    {
        public int Calls { get; private set; }

        public Task<ScanVerdict> ScanAsync(byte[] content, CancellationToken ct)
        {
            var step = script[Math.Min(Calls, script.Length - 1)];
            Calls++;
            return Task.FromResult(step());
        }
    }

    private static ServiceProvider ServicesFor(PatientSearchSeed seed, FakeNoteFileStore store, IVirusScanner scanner)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => seed.CreateDbContext());
        services.AddSingleton<INoteFileStore>(store);
        services.AddSingleton(scanner);
        return services.BuildServiceProvider();
    }

    private static Func<ScanVerdict> Clean => () => new ScanVerdict(true);
    private static Func<ScanVerdict> Infected => () => new ScanVerdict(false, "Eicar-Test-Signature");
    private static Func<ScanVerdict> Broken => () => throw new ScanException("clamd reported an error.");

    private static Task<(ScanStep Step, TimeSpan Delay)> RunAsync(ServiceProvider services, Guid documentId, Guid providerId) =>
        ScanWorker.ProcessAsync(
            new ScanJob(documentId, providerId), services.GetRequiredService<IServiceScopeFactory>(),
            new FakeTimeProvider(Now), NullLogger.Instance, Ct);

    [Fact]
    public async Task Process_CleanVerdict_MarksTheDocumentCleanAndKeepsTheObject()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending");
        await using var services = ServicesFor(seed, store, new ScriptedScanner(Clean));

        // Act
        var result = await RunAsync(services, document.Id, seed.ProviderId);

        // Assert
        result.Step.Should().Be(ScanStep.Done);
        var row = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        row.ScanStatus.Should().Be("clean");
        row.ScannedAt.Should().Be(Now);
        row.ObjectKey.Should().Be(document.ObjectKey);
        store.Objects.Should().ContainKey(document.ObjectKey!);
    }

    [Fact]
    public async Task Process_InfectedVerdict_BlocksTheDocument_DeletesTheObject_AndAudits()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending");
        await using var services = ServicesFor(seed, store, new ScriptedScanner(Infected));

        // Act
        var result = await RunAsync(services, document.Id, seed.ProviderId);

        // Assert
        result.Step.Should().Be(ScanStep.Done);
        var row = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        row.ScanStatus.Should().Be("infected");
        row.ScannedAt.Should().Be(Now);
        row.ObjectKey.Should().BeNull();
        row.DeletedAt.Should().BeNull();
        store.Objects.Should().BeEmpty();
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.ActorId == seed.ProviderId, Ct);
        audit.Action.Should().Be("external_document_blocked");
        audit.NewValue.Should().Be($"{document.Id} laudo");
    }

    [Fact]
    public async Task Process_ScannerErrors_RetryTwiceThenFailTheDocument()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending");
        var scanner = new ScriptedScanner(Broken);
        await using var services = ServicesFor(seed, store, scanner);

        // Act
        var first = await RunAsync(services, document.Id, seed.ProviderId);
        var afterFirst = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        var second = await RunAsync(services, document.Id, seed.ProviderId);
        var third = await RunAsync(services, document.Id, seed.ProviderId);
        var fourth = await RunAsync(services, document.Id, seed.ProviderId);

        // Assert
        first.Step.Should().Be(ScanStep.Retry);
        first.Delay.Should().BePositive();
        afterFirst.ScanStatus.Should().Be("pending");
        afterFirst.ScanAttempts.Should().Be(1);
        second.Step.Should().Be(ScanStep.Retry);
        second.Delay.Should().BeGreaterThan(first.Delay);
        third.Step.Should().Be(ScanStep.Done);
        var row = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        row.ScanStatus.Should().Be("failed");
        row.ScanAttempts.Should().Be(3);
        row.ObjectKey.Should().NotBeNull();
        fourth.Step.Should().Be(ScanStep.Done);
        scanner.Calls.Should().Be(3);
    }

    [Fact]
    public async Task Process_ScannerUnavailable_KeepsRetryingWithoutCountingAttempts_ThenScansWhenItIsBack()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending");
        Func<ScanVerdict> down = () => throw new ScannerUnavailableException("clamd is not reachable.");
        var scanner = new ScriptedScanner(down, down, down, down, down, down, down, down, down, down, Clean);
        await using var services = ServicesFor(seed, store, scanner);

        // Act
        var results = new List<(ScanStep Step, TimeSpan Delay)>();
        for (var i = 0; i < 10; i++)
        {
            results.Add(await RunAsync(services, document.Id, seed.ProviderId));
        }
        var whileDown = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        var back = await RunAsync(services, document.Id, seed.ProviderId);

        // Assert
        results.Should().OnlyContain(r => r.Step == ScanStep.Retry);
        results.Select(r => r.Delay).Should().BeInAscendingOrder().And.OnlyContain(d => d <= TimeSpan.FromMinutes(5));
        results[^1].Delay.Should().Be(TimeSpan.FromMinutes(5));
        whileDown.ScanStatus.Should().Be("pending");
        whileDown.ScanAttempts.Should().Be(0);
        back.Step.Should().Be(ScanStep.Done);
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct)).ScanStatus.Should().Be("clean");
    }

    [Fact]
    public async Task Process_ConnectionKeepsDropping_FailsTheDocumentAfterTheCap_ButAScannerThatIsDownDoesNot()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var lost = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending");
        var down = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending");
        Func<ScanVerdict> drops = () => throw new ScannerUnavailableException("clamd closed the connection.", null, connectionLost: true);
        Func<ScanVerdict> unreachable = () => throw new ScannerUnavailableException("clamd is not reachable.");
        var clock = new FakeTimeProvider(Now);
        await using var lostServices = ServicesFor(seed, store, new ScriptedScanner(drops));
        await using var downServices = ServicesFor(seed, store, new ScriptedScanner(unreachable));
        var scopesLost = lostServices.GetRequiredService<IServiceScopeFactory>();
        var scopesDown = downServices.GetRequiredService<IServiceScopeFactory>();

        // Act
        var first = await ScanWorker.ProcessAsync(new ScanJob(lost.Id, seed.ProviderId), scopesLost, clock, NullLogger.Instance, Ct);
        var firstDown = await ScanWorker.ProcessAsync(new ScanJob(down.Id, seed.ProviderId), scopesDown, clock, NullLogger.Instance, Ct);
        clock.Advance(ScanWorker.MaxConnectionLostDuration - TimeSpan.FromMinutes(1));
        var beforeCap = await ScanWorker.ProcessAsync(new ScanJob(lost.Id, seed.ProviderId), scopesLost, clock, NullLogger.Instance, Ct);
        clock.Advance(TimeSpan.FromMinutes(2));
        var atCap = await ScanWorker.ProcessAsync(new ScanJob(lost.Id, seed.ProviderId), scopesLost, clock, NullLogger.Instance, Ct);
        var downLate = await ScanWorker.ProcessAsync(new ScanJob(down.Id, seed.ProviderId), scopesDown, clock, NullLogger.Instance, Ct);

        // Assert
        first.Step.Should().Be(ScanStep.Retry);
        firstDown.Step.Should().Be(ScanStep.Retry);
        beforeCap.Step.Should().Be(ScanStep.Retry);
        atCap.Step.Should().Be(ScanStep.Done);
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == lost.Id, Ct)).ScanStatus.Should().Be("failed");
        downLate.Step.Should().Be(ScanStep.Retry);
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == down.Id, Ct)).ScanStatus.Should().Be("pending");
    }

    [Fact]
    public async Task Process_ARetryThatSucceeds_ClearsThePending()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending");
        await using var services = ServicesFor(seed, store, new ScriptedScanner(Broken, Clean));

        // Act
        var first = await RunAsync(services, document.Id, seed.ProviderId);
        var second = await RunAsync(services, document.Id, seed.ProviderId);

        // Assert
        first.Step.Should().Be(ScanStep.Retry);
        second.Step.Should().Be(ScanStep.Done);
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct)).ScanStatus.Should().Be("clean");
    }

    [Fact]
    public async Task Process_MissingObject_FailsTheDocumentWithoutScanning()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending");
        store.Objects.Clear();
        var scanner = new ScriptedScanner(Clean);
        await using var services = ServicesFor(seed, store, scanner);

        // Act
        var result = await RunAsync(services, document.Id, seed.ProviderId);

        // Assert
        result.Step.Should().Be(ScanStep.Done);
        scanner.Calls.Should().Be(0);
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct)).ScanStatus.Should().Be("failed");
    }

    [Fact]
    public async Task Process_NotPendingDeletedOrAnotherProvidersJob_IsSkipped()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var clean = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "clean");
        var deleted = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending", deletedAt: Now);
        var mine = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending");
        var scanner = new ScriptedScanner(Infected);
        await using var services = ServicesFor(seed, store, scanner);

        // Act
        await RunAsync(services, clean.Id, seed.ProviderId);
        await RunAsync(services, deleted.Id, seed.ProviderId);
        await RunAsync(services, mine.Id, other);

        // Assert
        scanner.Calls.Should().Be(0);
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == mine.Id, Ct)).ScanStatus.Should().Be("pending");
        (await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == clean.Id, Ct)).ScanStatus.Should().Be("clean");
    }

    [Fact]
    public async Task Process_InfectedButTheObjectCannotBeDeleted_KeepsTheKeyUntilTheSweepFinishesTheJob()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var document = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending");
        await using var services = ServicesFor(seed, store, new ScriptedScanner(Infected));
        var queue = new ScanQueue();
        store.FailDelete = true;

        // Act
        await RunAsync(services, document.Id, seed.ProviderId);
        var blocked = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        store.FailDelete = false;
        await ScanWorker.SweepAsync(services.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger.Instance, Ct);

        // Assert
        blocked.ScanStatus.Should().Be("infected");
        blocked.ObjectKey.Should().NotBeNull();
        var row = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id, Ct);
        row.ObjectKey.Should().BeNull();
        store.Objects.Should().BeEmpty();
        // The sweep reads every provider's pending rows (the dev database is shared): only ours matter.
        var leftovers = new List<ScanJob>();
        while (queue.Reader.TryRead(out var queued))
        {
            leftovers.Add(queued);
        }
        leftovers.Should().NotContain(j => j.ProviderId == seed.ProviderId);
    }

    [Fact]
    public async Task Sweep_RequeuesPendingDocumentsOnly()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var older = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending", uploadedAt: Now.AddHours(-2));
        var newer = await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending", uploadedAt: Now.AddHours(-1));
        await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "clean");
        await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "failed");
        await ExternalDocumentServiceTests.AddDocumentAsync(db, store, seed.ProviderId, patient, "pending", withObject: false, deletedAt: Now);
        await using var services = ServicesFor(seed, store, new ScriptedScanner(Clean));
        var queue = new ScanQueue();

        // Act
        await ScanWorker.SweepAsync(services.GetRequiredService<IServiceScopeFactory>(), queue, NullLogger.Instance, Ct);

        // Assert
        var queued = new List<ScanJob>();
        while (queue.Reader.TryRead(out var job))
        {
            queued.Add(job);
        }
        queued.Where(j => j.ProviderId == seed.ProviderId).Select(j => j.DocumentId).Should().Contain([older.Id, newer.Id]);
        queued.Where(j => j.ProviderId == seed.ProviderId).Should().HaveCount(2);
        queued.Where(j => j.ProviderId == seed.ProviderId).First().DocumentId.Should().Be(older.Id);
    }

    [Fact]
    public async Task UploadThenProcess_EndToEnd_MakesTheDocumentOpenable_AndAnEicarUploadBlocked()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Maria", "Silva", cancellationToken: Ct);
        var store = new FakeNoteFileStore();
        await using var db = seed.CreateDbContext();
        var queue = new ScanQueue();
        await using var services = ServicesFor(seed, store, new FakeVirusScanner());
        var eicar = "%PDF-1.4\nX5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"u8.ToArray();

        // Act
        var good = await ExternalDocumentService.UploadAsync(
            db, store, queue, seed.ProviderId, patient, new("a.pdf", "%PDF-1.4\nok"u8.ToArray(), "exame", null, null), Now, Ct);
        var bad = await ExternalDocumentService.UploadAsync(
            db, store, queue, seed.ProviderId, patient, new("b.pdf", eicar, "exame", null, null), Now, Ct);
        while (queue.Reader.TryRead(out var job))
        {
            await RunAsync(services, job.DocumentId, job.ProviderId);
        }

        db.ChangeTracker.Clear(); // the worker changed the rows through its own contexts
        // Assert
        (await ExternalDocumentService.OpenAsync(db, store, seed.ProviderId, good.Id!.Value, Now, Ct)).Outcome.Should().Be(ExternalDocumentOutcome.Ok);
        (await ExternalDocumentService.OpenAsync(db, store, seed.ProviderId, bad.Id!.Value, Now, Ct)).Outcome.Should().Be(ExternalDocumentOutcome.NotAvailable);
        var blocked = await db.ExternalDocuments.AsNoTracking().SingleAsync(d => d.Id == bad.Id, Ct);
        blocked.ScanStatus.Should().Be("infected");
        blocked.ObjectKey.Should().BeNull();
        (await db.AuditEntries.AsNoTracking().Where(a => a.ActorId == seed.ProviderId).Select(a => a.Action).ToListAsync(Ct))
            .Should().BeEquivalentTo(
                "external_document_uploaded", "external_document_uploaded", "external_document_blocked", "external_document_opened");
    }
}
