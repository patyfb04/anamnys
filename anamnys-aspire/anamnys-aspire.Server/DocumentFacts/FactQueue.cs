using System.Threading.Channels;

namespace Anamnys.Server.DocumentFacts;

// Ids only, never text.
public sealed record FactJob(Guid DocumentId, Guid ProviderId);

// In-memory only: a restart loses queued extractions; the facts already saved stay, and at start
// FactWorker.SweepAsync queues again every document not extracted since its read (up to
// FactWorker.MaxSweepAttempts started attempts; "Ler de novo" covers the rest). A document is queued at
// most once at a time; while its job is queued or running, IsBusy is true (the list and the facts panel
// show it as being read).
public sealed class FactQueue
{
    private readonly Channel<FactJob> channel = Channel.CreateUnbounded<FactJob>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<Guid> queued = [];
    private readonly HashSet<Guid> running = [];
    private readonly Lock gate = new();

    public ChannelReader<FactJob> Reader => channel.Reader;

    // False when the document is already queued (nothing is added). A document whose extraction is
    // running is queued again: its pages may have changed since the run started.
    public bool Enqueue(FactJob job)
    {
        lock (gate)
        {
            if (!queued.Add(job.DocumentId))
            {
                return false;
            }
        }
        if (channel.Writer.TryWrite(job))
        {
            return true;
        }
        lock (gate)
        {
            queued.Remove(job.DocumentId);
        }
        return false;
    }

    // The worker took the job: it is running, no longer queued.
    public void Begin(FactJob job)
    {
        lock (gate)
        {
            queued.Remove(job.DocumentId);
            running.Add(job.DocumentId);
        }
    }

    public void End(FactJob job)
    {
        lock (gate)
        {
            running.Remove(job.DocumentId);
        }
    }

    public bool IsBusy(Guid documentId)
    {
        lock (gate)
        {
            return queued.Contains(documentId) || running.Contains(documentId);
        }
    }
}
