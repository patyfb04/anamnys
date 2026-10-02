namespace Anamnys.Server.Data.Entities;

// SourceFactId (a fact extracted from an external document) is unmapped: provider-typed
// entries never have one, and the column is nullable.
public class MedicationEntry
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public string Drug { get; set; } = "";
    public string? Dose { get; set; }
    public string? Posology { get; set; }
    public DateOnly StartedOn { get; set; }
    public DateOnly? EndedOn { get; set; }
}
