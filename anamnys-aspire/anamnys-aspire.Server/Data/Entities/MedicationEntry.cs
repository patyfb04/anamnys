namespace Anamnys.Server.Data.Entities;

// TreatmentId groups the records of one treatment (a dose change shares it; a new record gets its own id).
// SourceFactId is the confirmed fact of an external document the record was made from; the database clears it
// when the document is deleted, and FromDocument is what remembers the record was not typed by the provider
// (design/specs/2026-10-27-medication-timeline-design.md §2).
public class MedicationEntry
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public string Drug { get; set; } = "";
    public string? Dose { get; set; }
    public string? Posology { get; set; }
    public DateOnly StartedOn { get; set; }
    public DateOnly? EndedOn { get; set; }
    public Guid? SourceFactId { get; set; }
    public Guid TreatmentId { get; set; }
    public bool FromDocument { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
