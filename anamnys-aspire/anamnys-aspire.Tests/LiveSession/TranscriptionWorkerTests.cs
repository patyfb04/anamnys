using Anamnys.Server.Data;
using Anamnys.Server.LiveSession;
using Anamnys.Server.NoteStructuring;
using Anamnys.Server.Themes;
using Anamnys.Tests.NoteStructuring;
using Anamnys.Tests.Patients;
using Anamnys.Tests.Themes;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.LiveSession;

[Collection(SharedAppHostCollection.Name)]
public class TranscriptionWorkerTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunJob_WhenProgressNotificationThrows_FailsNoteAndNotifiesClients()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var buffers = new RecordingBuffers(new FakeTimeProvider(Now));
        var queue = new TranscriptionQueue();
        var id = Guid.NewGuid();
        buffers.Begin(id, new RecordingMeta(seed.ProviderId, patient, null, "DAP", "pt", Now.AddMinutes(-10)));
        buffers.Append(id, seed.ProviderId, 0, [1, 0, 2, 0]);
        await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, id, Now, Ct);
        queue.Reader.TryRead(out var job).Should().BeTrue();
        var proxy = new RecordingProxy(failOn: "NoteProgress");
        var services = new ServiceCollection()
            .AddScoped(_ => seed.CreateDbContext())
            .AddSingleton<ITranscriber>(new FakeTranscriber(_ => "texto"))
            .BuildServiceProvider();

        // Act
        await TranscriptionWorker.RunJobAsync((TranscriptionJob)job!, services.GetRequiredService<IServiceScopeFactory>(),
            new FakeHubContext(proxy), new FakeTimeProvider(Now.AddMinutes(1)), NullLogger.Instance, Ct);

        // Assert
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == job!.NoteId, Ct);
        note.Status.Should().Be("Failed");
        note.FailureReason.Should().Be("transcription_error");
        proxy.Methods.Should().Contain("NoteFailed");
    }

    [Fact]
    public async Task RunJob_Success_SendsReadyThenStructures()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var structurer = new FakeNoteStructurer((_, _) =>
        [
            new("data", "Paciente relata insônia."), new("assessment", ""), new("plan", ""),
        ]);
        var (job, proxy, services) = await FinalizeAsync(seed, patient, _ => "Paciente relata insônia.", structurer);

        // Act
        await TranscriptionWorker.RunJobAsync(job, services.GetRequiredService<IServiceScopeFactory>(),
            new FakeHubContext(proxy), new FakeTimeProvider(Now.AddMinutes(1)), NullLogger.Instance, Ct);

        // Assert
        await using var db = seed.CreateDbContext();
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == job.NoteId, Ct);
        note.Status.Should().Be("ReadyForReview");
        note.StructureStatus.Should().Be("done");
        (await db.NoteSections.CountAsync(s => s.NoteId == job.NoteId, Ct)).Should().Be(3);
        proxy.Methods.Should().ContainInOrder("NoteReady", "NoteProgress", "NoteStructured");
    }

    [Fact]
    public async Task RunJob_NoSpeech_DoesNotStructure()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var structurer = new FakeNoteStructurer((_, _) => []);
        var (job, proxy, services) = await FinalizeAsync(seed, patient, _ => "", structurer);

        // Act
        await TranscriptionWorker.RunJobAsync(job, services.GetRequiredService<IServiceScopeFactory>(),
            new FakeHubContext(proxy), new FakeTimeProvider(Now.AddMinutes(1)), NullLogger.Instance, Ct);

        // Assert
        await using var db = seed.CreateDbContext();
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == job.NoteId, Ct);
        note.StructureStatus.Should().BeNull();
        structurer.Calls.Should().Be(0);
        proxy.Methods.Should().Contain("NoteFailed").And.NotContain("NoteStructured");
    }

    [Fact]
    public async Task RunStructure_StructurerFails_SendsStructureFailed()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        var structurer = new FakeNoteStructurer((_, _) => throw new InvalidOperationException("boom"));
        var (job, proxy, services) = await FinalizeAsync(seed, patient, _ => "texto", structurer);
        await using (var db = seed.CreateDbContext())
        {
            await db.Notes.Where(n => n.Id == job.NoteId).ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, "ReadyForReview")
                .SetProperty(n => n.RawTranscript, "texto")
                .SetProperty(n => n.StructureStatus, "pending"), Ct);
        }

        // Act
        await TranscriptionWorker.RunStructureAsync(job.NoteId, seed.ProviderId, services.GetRequiredService<IServiceScopeFactory>(),
            new FakeHubContext(proxy), new FakeTimeProvider(Now.AddMinutes(1)), NullLogger.Instance, Ct);

        // Assert
        await using var verify = seed.CreateDbContext();
        var note = await verify.Notes.AsNoTracking().SingleAsync(n => n.Id == job.NoteId, Ct);
        note.StructureStatus.Should().Be("failed");
        note.StructureFailure.Should().Be("model_error");
        note.Status.Should().Be("ReadyForReview");
        proxy.Methods.Should().Contain("NoteStructureFailed");
    }

    [Fact]
    public async Task RunStructure_WithRequestedFormat_StructuresAndSetsFormat()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var patient = await seed.AddPatientAsync("Ana", "Silva", cancellationToken: Ct);
        string? requested = null;
        var structurer = new FakeNoteStructurer((_, format) =>
        {
            requested = format;
            return [new("subjective", "texto"), new("objective", ""), new("assessment", ""), new("plan", "")];
        });
        var (job, proxy, services) = await FinalizeAsync(seed, patient, _ => "texto", structurer);
        await using (var db = seed.CreateDbContext())
        {
            await db.Notes.Where(n => n.Id == job.NoteId).ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, "ReadyForReview")
                .SetProperty(n => n.RawTranscript, "texto")
                .SetProperty(n => n.StructureStatus, "pending"), Ct);
        }

        // Act
        await TranscriptionWorker.RunStructureAsync(job.NoteId, seed.ProviderId, services.GetRequiredService<IServiceScopeFactory>(),
            new FakeHubContext(proxy), new FakeTimeProvider(Now.AddMinutes(1)), NullLogger.Instance, Ct, "SOAP");

        // Assert
        requested.Should().Be("SOAP");
        await using var verify = seed.CreateDbContext();
        var note = await verify.Notes.AsNoTracking().SingleAsync(n => n.Id == job.NoteId, Ct);
        note.Format.Should().Be("SOAP");
        note.StructureStatus.Should().Be("done");
        proxy.Methods.Should().Contain("NoteStructured");
    }

    private static async Task<(TranscriptionJob Job, RecordingProxy Proxy, ServiceProvider Services)> FinalizeAsync(
        PatientSearchSeed seed, Guid patient, Func<float[], string> transcribe, INoteStructurer structurer)
    {
        await using var db = seed.CreateDbContext();
        var buffers = new RecordingBuffers(new FakeTimeProvider(Now));
        var queue = new TranscriptionQueue();
        var id = Guid.NewGuid();
        buffers.Begin(id, new RecordingMeta(seed.ProviderId, patient, null, "DAP", "pt", Now.AddMinutes(-10)));
        buffers.Append(id, seed.ProviderId, 0, [1, 0, 2, 0]);
        await LiveRecordings.FinalizeAsync(db, buffers, queue, seed.ProviderId, id, Now, Ct);
        queue.Reader.TryRead(out var job).Should().BeTrue();
        var services = new ServiceCollection()
            .AddScoped(_ => seed.CreateDbContext())
            .AddSingleton<ITranscriber>(new FakeTranscriber(transcribe))
            .AddSingleton(structurer)
            .AddSingleton<IThemeSuggester>(new FakeThemeSuggester())
            .BuildServiceProvider();
        return ((TranscriptionJob)job!, new RecordingProxy(), services);
    }

    private sealed class RecordingProxy(string? failOn = null) : IClientProxy
    {
        public List<string> Methods { get; } = [];

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Methods.Add(method);
            return method == failOn ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }
    }

    private sealed class FakeHubContext(IClientProxy proxy) : IHubContext<ProgressHub>
    {
        public IHubClients Clients { get; } = new FakeClients(proxy);

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FakeClients(IClientProxy proxy) : IHubClients
    {
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
        public IClientProxy Group(string groupName) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }
}
