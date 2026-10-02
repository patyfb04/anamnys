namespace Anamnys.Server.Data.Entities;

public class PatientDiagnosis
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public string Description { get; set; } = "";
    public string? IcdCode { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateOnly? ResolvedOn { get; set; }
}
