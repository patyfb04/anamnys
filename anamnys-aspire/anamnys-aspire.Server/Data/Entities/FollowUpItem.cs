namespace Anamnys.Server.Data.Entities;

// A point marked "para retomar" on a note (design/specs/2026-10-30-plan-adherence-design.md §2). NoteId is the note it
// was marked on, ExcerptRefId the passage it was marked from (optional); ResolvedNoteId the later note it was taken up
// in, when the provider resolved it from one. Reopening clears the three Resolved* fields.
public class FollowUpItem
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? NoteId { get; set; }
    public Guid? ExcerptRefId { get; set; }
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedNoteId { get; set; }
    public Guid? ResolvedById { get; set; }
}
