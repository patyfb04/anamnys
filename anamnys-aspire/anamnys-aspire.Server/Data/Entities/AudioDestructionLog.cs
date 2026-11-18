namespace Anamnys.Server.Data.Entities;

// One row per recording whose audio was destroyed (design/specs/2026-11-16-transient-audio-tab-capture-design.md §2.2).
// SessionId holds the recording id. RecordingMode: session | dictation. Sources: every source used, in order of first use
// (in_person | online_capture | online_speaker). Outcome: transcribed | failed | abandoned. Ids and codes only, no audio.
public class AudioDestructionLog
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? NoteId { get; set; }
    public string RecordingMode { get; set; } = "";
    public List<string> Sources { get; set; } = [];
    public DateTimeOffset DestroyedAt { get; set; }
    public string Outcome { get; set; } = "";
}
