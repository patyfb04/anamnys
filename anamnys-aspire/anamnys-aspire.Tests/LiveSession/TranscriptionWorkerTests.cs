using Anamnys.Server.Data;
using Anamnys.Server.LiveSession;
using Anamnys.Tests.Patients;
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
        await TranscriptionWorker.RunJobAsync(job!, services.GetRequiredService<IServiceScopeFactory>(),
            new FakeHubContext(proxy), new FakeTimeProvider(Now.AddMinutes(1)), NullLogger.Instance, Ct);

        // Assert
        var note = await db.Notes.AsNoTracking().SingleAsync(n => n.Id == job!.NoteId, Ct);
        note.Status.Should().Be("Failed");
        note.FailureReason.Should().Be("transcription_error");
        proxy.Methods.Should().Contain("NoteFailed");
    }

    private sealed class RecordingProxy(string failOn) : IClientProxy
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
