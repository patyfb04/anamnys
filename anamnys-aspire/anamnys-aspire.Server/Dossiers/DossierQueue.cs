using System.Threading.Channels;

namespace Anamnys.Server.Dossiers;

public sealed record DossierJob(Guid DossierId, Guid ProviderId);

// In-memory only: a restart loses the queue, and DossierSweep re-enqueues the rows still queued (running ones fail).
public sealed class DossierQueue
{
    private readonly Channel<DossierJob> _channel = Channel.CreateUnbounded<DossierJob>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<DossierJob> Reader => _channel.Reader;

    public void Enqueue(DossierJob job) => _channel.Writer.TryWrite(job);
}
