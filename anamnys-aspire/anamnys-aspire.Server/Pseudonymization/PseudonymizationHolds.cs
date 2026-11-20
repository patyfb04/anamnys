using Anamnys.Server.LiveSession;

namespace Anamnys.Server.Pseudonymization;

public sealed record HeldText(Guid PatientId, string Text, DateTimeOffset Until)
{
    private bool PrintMembers(System.Text.StringBuilder builder) => false;

    public override string ToString() => nameof(HeldText);
}

// The original text of a note whose pseudonymisation failed, kept in memory only so "Tentar de novo" can run again
// (design/specs/2026-11-17-pseudonymization-design.md §2). Bounded by the live session's in-memory retention
// (LiveSessionRules.BufferIdleTimeout, 10 minutes) from the first failure; a retry that fails again keeps the first
// deadline. Never written anywhere; a restart loses it on purpose. The held characters are zeroed when the entry is
// taken, forgotten (discard) or when its timer fires — whenever it fires: the timer belongs to its entry, so an early or
// late tick still removes and zeroes that entry. (The managed strings handed in and out cannot be zeroed and are dropped
// by the caller.)
public sealed class PseudonymizationHolds(TimeProvider clock) : IDisposable
{
    public static TimeSpan MaxHold => LiveSessionRules.BufferIdleTimeout;

    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Lock _lock = new();

    public DateTimeOffset Hold(Guid noteId, Guid providerId, Guid patientId, string text, DateTimeOffset? until = null)
    {
        var deadline = until ?? clock.GetUtcNow() + MaxHold;
        var delay = deadline - clock.GetUtcNow();
        if (delay <= TimeSpan.Zero)
        {
            return deadline;
        }
        lock (_lock)
        {
            Remove(noteId);
            var entry = new Entry(noteId, providerId, patientId, text.ToCharArray(), deadline);
            _entries[noteId] = entry;
            entry.Timer = clock.CreateTimer(state => Expire((Entry)state!), entry, delay, Timeout.InfiniteTimeSpan);
        }
        return deadline;
    }

    public bool IsHeld(Guid noteId, Guid providerId)
    {
        lock (_lock)
        {
            return Live(noteId) is { } entry && entry.ProviderId == providerId;
        }
    }

    // The held text, once: the entry is gone after this call. Null for another provider's note or an expired entry.
    public HeldText? Take(Guid noteId, Guid providerId)
    {
        lock (_lock)
        {
            if (Live(noteId) is not { } entry || entry.ProviderId != providerId)
            {
                return null;
            }
            var held = new HeldText(entry.PatientId, new string(entry.Text), entry.Until);
            Remove(noteId);
            return held;
        }
    }

    // Drops and zeroes a note's held text at once (the note or its patient record is gone).
    public void Forget(Guid noteId)
    {
        lock (_lock)
        {
            Remove(noteId);
        }
    }

    // Test hook: the live held characters of a note, so a test can check they are zeroed later.
    internal char[]? HeldChars(Guid noteId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(noteId, out var entry) ? entry.Text : null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var id in _entries.Keys.ToList())
            {
                Remove(id);
            }
        }
    }

    // The entry when it is still within its deadline; an entry past it is removed (and zeroed) here.
    private Entry? Live(Guid noteId)
    {
        if (!_entries.TryGetValue(noteId, out var entry))
        {
            return null;
        }
        if (entry.Until > clock.GetUtcNow())
        {
            return entry;
        }
        Remove(noteId);
        return null;
    }

    // The timer of this very entry fired (early, on time or late): it goes, unless it was already replaced or removed.
    private void Expire(Entry entry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(entry.NoteId, out var current) && ReferenceEquals(current, entry))
            {
                Remove(entry.NoteId);
            }
        }
    }

    private void Remove(Guid noteId)
    {
        if (_entries.Remove(noteId, out var entry))
        {
            entry.Timer?.Dispose();
            Array.Clear(entry.Text);
        }
    }

    private sealed class Entry(Guid noteId, Guid providerId, Guid patientId, char[] text, DateTimeOffset until)
    {
        public Guid NoteId { get; } = noteId;
        public Guid ProviderId { get; } = providerId;
        public Guid PatientId { get; } = patientId;
        public char[] Text { get; } = text;
        public DateTimeOffset Until { get; } = until;
        public ITimer? Timer { get; set; }
    }
}
