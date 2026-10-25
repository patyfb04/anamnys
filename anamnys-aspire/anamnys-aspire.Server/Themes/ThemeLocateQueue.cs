using System.Threading.Channels;

namespace Anamnys.Server.Themes;

// A background "Localizar trechos" (design/specs/2026-10-24-excerpt-references-design.md §3): one note,
// optionally one tag. SectionOnly: the provider chose the tag's section, so only that section is searched.
// Ids only, never text.
public sealed record ThemeLocateJob(Guid ProviderId, Guid NoteId, Guid? TagId = null, bool SectionOnly = false);

// In-memory only: a restart loses queued jobs; the tags keep their section reference and the note's
// "Localizar trechos" action runs it again. Pending jobs are de-duplicated by (provider, note, tag): a
// repeated enqueue is dropped, and a pending note-wide job absorbs the per-tag jobs of that note, except the SectionOnly ones (the provider chose the section: they
// are never absorbed); the others are dropped on enqueue, and skipped by the worker when the note-wide job was queued after them.
public sealed class ThemeLocateQueue
{
    private readonly Channel<ThemeLocateJob> channel = Channel.CreateUnbounded<ThemeLocateJob>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<(Guid ProviderId, Guid NoteId, Guid? TagId, bool SectionOnly)> pending = [];
    private readonly Lock gate = new();

    public ChannelReader<ThemeLocateJob> Reader => channel.Reader;

    // False when an equivalent job is already pending (nothing is queued).
    public bool Enqueue(ThemeLocateJob job)
    {
        lock (gate)
        {
            if ((!job.SectionOnly && pending.Contains((job.ProviderId, job.NoteId, null, false))) || !pending.Add(Key(job)))
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
            pending.Remove(Key(job));
        }
        return false;
    }

    // Called by the worker for each job it reads, before running it: the job is no longer pending (a new
    // enqueue for it is accepted from now on). False when the job is covered by a pending note-wide job.
    public bool Begin(ThemeLocateJob job)
    {
        lock (gate)
        {
            pending.Remove(Key(job));
            return job.TagId is null || job.SectionOnly || !pending.Contains((job.ProviderId, job.NoteId, null, false));
        }
    }

    private static (Guid, Guid, Guid?, bool) Key(ThemeLocateJob job) => (job.ProviderId, job.NoteId, job.TagId, job.SectionOnly);
}
