namespace Anamnys.Server.Data.Entities;

// Read-only column subset of "Notes": the patient list and detail page need status,
// format and dates. Nothing writes notes through EF yet, and InputMode is NOT NULL and
// unmapped.
public class Note
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public string Status { get; set; } = "";
    public string Format { get; set; } = "";
    public DateTimeOffset? SignedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
