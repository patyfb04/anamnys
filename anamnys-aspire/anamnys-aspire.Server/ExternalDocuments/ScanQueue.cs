using System.Threading.Channels;

namespace Anamnys.Server.ExternalDocuments;

public sealed record ScanJob(Guid DocumentId, Guid ProviderId);

// In-memory only: a restart loses queued jobs, and the worker's start-up sweep re-queues every pending row.
public sealed class ScanQueue
{
    private readonly Channel<ScanJob> _channel = Channel.CreateUnbounded<ScanJob>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<ScanJob> Reader => _channel.Reader;

    public void Enqueue(ScanJob job) => _channel.Writer.TryWrite(job);
}
