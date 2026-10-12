using System.Threading.Channels;

namespace Anamnys.Server.LiveSession;

public sealed record TranscriptionJob(Guid NoteId, Guid ProviderId, string Language, PcmAudio Audio);

// In-memory only: a restart loses queued audio on purpose (spec §2). Positions are 1-based.
public sealed class TranscriptionQueue
{
    private readonly Channel<TranscriptionJob> _channel = Channel.CreateUnbounded<TranscriptionJob>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly List<Guid> _pending = [];
    private readonly Lock _lock = new();

    public ChannelReader<TranscriptionJob> Reader => _channel.Reader;

    public void Enqueue(TranscriptionJob job)
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
