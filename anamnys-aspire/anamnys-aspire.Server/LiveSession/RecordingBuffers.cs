using System.Security.Cryptography;

namespace Anamnys.Server.LiveSession;

public sealed record RecordingMeta(Guid ProviderId, Guid PatientId, Guid? AppointmentId, string Format, string Language, DateTimeOffset StartedAt);

public enum BeginStatus { Opened, Resumed, Forbidden }

public enum ChunkStatus { Accepted, Duplicate, Gap, Expired, Forbidden, TooLong }

public sealed record ChunkResult(ChunkStatus Status, int LastSeq);

public sealed record TakenRecording(RecordingMeta Meta, PcmAudio Audio);

// In-memory audio of recordings in progress, keyed by recording id so a reconnect continues the
// same buffer (spec §4.2). Never written anywhere else.
public sealed class RecordingBuffers(TimeProvider clock, long maxAudioBytes = LiveSessionRules.MaxAudioBytes)
{
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Lock _lock = new();

    public BeginStatus Begin(Guid recordingId, RecordingMeta meta)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(recordingId, out var existing))
            {
                if (existing.Meta.ProviderId != meta.ProviderId)
                {
                    return BeginStatus.Forbidden;
                }
                existing.Touch(clock.GetUtcNow());
                return BeginStatus.Resumed;
            }
            _entries[recordingId] = new Entry(meta, clock.GetUtcNow());
            return BeginStatus.Opened;
        }
    }

    public ChunkResult Append(Guid recordingId, Guid providerId, int seq, ReadOnlySpan<byte> chunk)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(recordingId, out var entry))
            {
                return new ChunkResult(ChunkStatus.Expired, -1);
            }
            if (entry.Meta.ProviderId != providerId)
            {
                return new ChunkResult(ChunkStatus.Forbidden, -1);
            }
            if (seq <= entry.LastSeq)
            {
                return new ChunkResult(ChunkStatus.Duplicate, entry.LastSeq);
            }
            if (seq != entry.LastSeq + 1)
            {
                return new ChunkResult(ChunkStatus.Gap, entry.LastSeq);
            }
            if (entry.Length + chunk.Length > maxAudioBytes)
            {
                return new ChunkResult(ChunkStatus.TooLong, entry.LastSeq);
            }
            entry.Write(chunk);
            entry.LastSeq = seq;
            entry.Touch(clock.GetUtcNow());
            return new ChunkResult(ChunkStatus.Accepted, seq);
        }
    }

    public RecordingMeta? Peek(Guid recordingId, Guid providerId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(recordingId, out var entry) && entry.Meta.ProviderId == providerId ? entry.Meta : null;
        }
    }

    public TakenRecording? Take(Guid recordingId, Guid providerId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(recordingId, out var entry) || entry.Meta.ProviderId != providerId)
            {
                return null;
            }
            _entries.Remove(recordingId);
            return new TakenRecording(entry.Meta, entry.Detach());
        }
    }

    public int SweepExpired()
    {
        var cutoff = clock.GetUtcNow() - LiveSessionRules.BufferIdleTimeout;
        lock (_lock)
        {
            var expired = _entries.Where(kv => kv.Value.LastActivity < cutoff).Select(kv => kv.Key).ToList();
            foreach (var id in expired)
            {
                _entries[id].Detach().Destroy();
                _entries.Remove(id);
            }
            return expired.Count;
        }
    }

    private sealed class Entry(RecordingMeta meta, DateTimeOffset now)
    {
        private byte[] _bytes = new byte[64 * 1024];

        public RecordingMeta Meta { get; } = meta;
        public int LastSeq { get; set; } = -1;
        public int Length { get; private set; }
        public DateTimeOffset LastActivity { get; private set; } = now;

        public void Touch(DateTimeOffset at) => LastActivity = at;

        public void Write(ReadOnlySpan<byte> chunk)
        {
            if (Length + chunk.Length > _bytes.Length)
            {
                var grown = new byte[Math.Max(_bytes.Length * 2, Length + chunk.Length)];
                _bytes.AsSpan(0, Length).CopyTo(grown);
                CryptographicOperations.ZeroMemory(_bytes);
                _bytes = grown;
            }
            chunk.CopyTo(_bytes.AsSpan(Length));
            Length += chunk.Length;
        }

        public PcmAudio Detach()
        {
            var audio = new PcmAudio(_bytes, Length);
            _bytes = [];
            Length = 0;
            return audio;
        }
    }
}
