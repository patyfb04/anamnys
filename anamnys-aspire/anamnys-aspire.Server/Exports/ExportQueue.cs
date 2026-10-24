using System.Threading.Channels;

namespace Anamnys.Server.Exports;

public sealed record ExportJob(Guid ExportId, Guid ProviderId);

// In-memory only: a restart loses queued jobs, and ExportSweep fails their rows.
public sealed class ExportQueue
{
    private readonly Channel<ExportJob> _channel = Channel.CreateUnbounded<ExportJob>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<ExportJob> Reader => _channel.Reader;

    public void Enqueue(ExportJob job) => _channel.Writer.TryWrite(job);
}
