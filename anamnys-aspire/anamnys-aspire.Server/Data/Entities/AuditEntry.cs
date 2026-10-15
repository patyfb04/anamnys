namespace Anamnys.Server.Data.Entities;

// Append-only audit trail of note and document mutations
// (design/specs/2026-10-13-note-review-and-signing-design.md §3).
public class AuditEntry
{
    public Guid Id { get; set; }
    public Guid? NoteId { get; set; }
    public Guid? DocumentId { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string? FieldChanged { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public DateTimeOffset At { get; set; }
}
