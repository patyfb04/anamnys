namespace Anamnys.Server.Data.Entities;

// One section of a structured note (design/specs/2026-10-12-note-structuring-design.md §3).
// GeneratedAt marks the AI draft; EditedAt (delivery 3) marks the provider's edit.
public class NoteSection
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
    public int Position { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
}
