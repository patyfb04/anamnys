using System.Threading.Channels;

namespace Anamnys.Server.LiveSession;

public abstract record NoteJob(Guid NoteId, Guid ProviderId);

// RecordingId and Meta go to the destruction record once the audio is zeroed (F-26).
public sealed record TranscriptionJob(Guid NoteId, Guid ProviderId, string Language, PcmAudio Audio, Guid RecordingId, RecordingMeta Meta)
    : NoteJob(NoteId, ProviderId);

public sealed record StructureJob(Guid NoteId, Guid ProviderId, string Format) : NoteJob(NoteId, ProviderId);

// F-27: a text waiting for the pseudonymisation step (the text of "Nota com IA", or a retry of a failed note). In memory
// only; HeldUntil keeps a retry's original deadline. The text never appears in ToString.
public sealed record PseudonymizeJob(Guid NoteId, Guid ProviderId, Guid PatientId, string Text, DateTimeOffset? HeldUntil = null)
    : NoteJob(NoteId, ProviderId)
{
    protected override bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"NoteId = {NoteId}, ProviderId = {ProviderId}, PatientId = {PatientId}");
        return true;
    }
}

// In-memory only: a restart loses queued audio on purpose (spec §2). Positions are 1-based.
public sealed class TranscriptionQueue
{
    private readonly Channel<NoteJob> _channel = Channel.CreateUnbounded<NoteJob>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly List<Guid> _pending = [];
    private readonly Lock _lock = new();

    public ChannelReader<NoteJob> Reader => _channel.Reader;

    public void Enqueue(NoteJob job)
    {
        lock (_lock)
        {
            _pending.Add(job.NoteId);
        }
        _channel.Writer.TryWrite(job);
    }

    public void MarkStarted(Guid noteId)
    {
        lock (_lock)
        {
            _pending.Remove(noteId);
        }
    }

    public int PositionOf(Guid noteId)
    {
        lock (_lock)
        {
            return _pending.IndexOf(noteId) + 1;
        }
    }
}
