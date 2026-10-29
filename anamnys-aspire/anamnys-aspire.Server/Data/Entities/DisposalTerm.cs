namespace Anamnys.Server.Data.Entities;

// The disposal term of a discarded patient record (design/specs/2026-10-28-retention-panel-design.md §2, §3).
// One per patient (DisposalTerms_PatientId_key); the patient row is kept (DisposalTerms_Patient_fk RESTRICT).
// ObjectKey is the PDF in the private bucket, ids only; Counts holds what was deleted, by kind (no PHI).
public class DisposalTerm
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string ObjectKey { get; set; } = "";
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public int GuardYears { get; set; }
    public string Basis { get; set; } = "";
    public Dictionary<string, int> Counts { get; set; } = [];

    // The deadline that had ended when the record was discarded (the patient's RetentionUntil at that moment).
    public DateOnly RetentionUntil { get; set; }
}
