namespace Anamnys.Server.Data.Entities;

// A passage of a note the provider marked as said by the patient (design/specs/2026-10-30-patient-quotes-design.md §2).
// Text is the selected passage, never edited; ExcerptRefId points back to it. TermId is the optional theme. A removal keeps
// the row (RemovedAt and RemovedById go together). Rows from before F-15 have no AddedById. No ProviderId: the note scopes it.
public class PatientQuote
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public Guid? TermId { get; set; }
    public string Text { get; set; } = "";
    public Guid? ExcerptRefId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? AddedById { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public Guid? RemovedById { get; set; }
}
