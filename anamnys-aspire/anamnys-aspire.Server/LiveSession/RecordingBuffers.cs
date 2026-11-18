using System.Security.Cryptography;

namespace Anamnys.Server.LiveSession;

// Mode: session | dictation. Sources: every source used, in order of first use (RecordingSources).
public sealed record RecordingMeta(
    Guid ProviderId, Guid PatientId, Guid? AppointmentId, string Format, string Language, DateTimeOffset StartedAt,
    string Mode, IReadOnlyList<string> Sources);

// Where the audio of a recording comes from (design/specs/2026-11-16-transient-audio-tab-capture-design.md §2.2).
public static class RecordingSources
{
    public const string InPerson = "in_person";
    public const string OnlineCapture = "online_capture";
    public const string OnlineSpeaker = "online_speaker";

    public static bool IsKnown(string? source) => source is InPerson or OnlineCapture or OnlineSpeaker;
}

public enum BeginStatus { Opened, Resumed, Forbidden, ModeMismatch }

public enum SourceStatus { Added, Expired, Forbidden, NotSession }

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
                // A resume never changes the mode: the consent gate ran for the mode the buffer was opened with.
                if (existing.Meta.Mode != meta.Mode)
                {
                    return BeginStatus.ModeMismatch;
                }
                existing.Touch(clock.GetUtcNow());
                // A resume names the source it continues with: kept when new.
                existing.AddSources(meta.Sources);
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

    // Adds a source to a session recording in progress (a mid-recording switch); the buffer and the sequence are untouched.
    public SourceStatus AddSource(Guid recordingId, Guid providerId, string source)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(recordingId, out var entry))
            {
                return SourceStatus.Expired;
            }
            if (entry.Meta.ProviderId != providerId)
            {
                return SourceStatus.Forbidden;
            }
            if (entry.Meta.Mode != LiveRecordings.SessionMode)
            {
                return SourceStatus.NotSession;
            }
            entry.AddSources([source]);
            entry.Touch(clock.GetUtcNow());
            return SourceStatus.Added;
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

    // Zeroes and drops the idle buffers under the lock; returns their ids and metas so the caller can record the
    // destruction outside it.
    public IReadOnlyList<(Guid RecordingId, RecordingMeta Meta)> SweepExpired()
    {
        var cutoff = clock.GetUtcNow() - LiveSessionRules.BufferIdleTimeout;
        lock (_lock)
        {
            var expired = _entries.Where(kv => kv.Value.LastActivity < cutoff).Select(kv => (kv.Key, kv.Value.Meta)).ToList();
            foreach (var (id, _) in expired)
            {
                _entries[id].Detach().Destroy();
                _entries.Remove(id);
            }
            return expired;
        }
    }

    // Test hook (F-26 proof): the live backing array of a recording in progress, so a test can check it is zeroed later.
    internal byte[]? BackingBytes(Guid recordingId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(recordingId, out var entry) ? entry.Bytes : null;
        }
    }

    private sealed class Entry(RecordingMeta meta, DateTimeOffset now)
    {
        private byte[] _bytes = new byte[64 * 1024];

        // Replaced (never mutated) under the buffers' lock, so a meta handed out earlier stays as it was.
        public RecordingMeta Meta { get; private set; } = meta;

        public void AddSources(IEnumerable<string> sources)
        {
            var added = sources.Where(s => !Meta.Sources.Contains(s)).Distinct().ToList();
            if (added.Count > 0)
            {
                Meta = Meta with { Sources = [.. Meta.Sources, .. added] };
            }
        }
        public byte[] Bytes => _bytes;
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
