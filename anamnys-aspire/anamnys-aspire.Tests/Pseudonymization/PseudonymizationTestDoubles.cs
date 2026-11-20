using Anamnys.Server.LiveSession;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Anamnys.Tests.Pseudonymization;

// Formats every entry (with its exception), so a test can check that no log line carries a name.
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
}

// A clock whose timers never fire by themselves: a test fires them by hand, at any time (early or late).
internal sealed class ManualTimerClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public List<ManualTimer> Timers { get; } = [];

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void FireAll()
    {
        foreach (var timer in Timers.ToList())
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state);
        Timers.Add(timer);
        return timer;
    }

    // Fire runs the callback even after Dispose: a real timer can still deliver a tick that was already queued.
    internal sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

// Records the hub methods sent to a note's group.
internal sealed class RecordingHub : IHubContext<ProgressHub>, IHubClients, IClientProxy
{
    public List<(string Method, object?[] Args)> Sent { get; } = [];

    IHubClients IHubContext<ProgressHub>.Clients => this;
    IGroupManager IHubContext<ProgressHub>.Groups => throw new NotSupportedException();
    IClientProxy IHubClients<IClientProxy>.All => this;
    IClientProxy IHubClients<IClientProxy>.AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
    IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => this;
    IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> connectionIds) => this;
    IClientProxy IHubClients<IClientProxy>.Group(string groupName) => this;
    IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groupNames) => this;
    IClientProxy IHubClients<IClientProxy>.GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
    IClientProxy IHubClients<IClientProxy>.User(string userId) => this;
    IClientProxy IHubClients<IClientProxy>.Users(IReadOnlyList<string> userIds) => this;

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        Sent.Add((method, args));
        return Task.CompletedTask;
    }
}
