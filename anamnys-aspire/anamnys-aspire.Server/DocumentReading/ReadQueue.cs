using System.Threading.Channels;

namespace Anamnys.Server.DocumentReading;

public sealed record ReadJob(Guid DocumentId, Guid ProviderId);

// In-memory only: a restart loses queued jobs, and the worker's start-up sweep re-queues every pending
// or interrupted read.
public sealed class ReadQueue
{
    private readonly Channel<ReadJob> _channel = Channel.CreateUnbounded<ReadJob>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<ReadJob> Reader => _channel.Reader;

    public void Enqueue(ReadJob job) => _channel.Writer.TryWrite(job);
}
