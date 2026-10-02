namespace Anamnys.Server.Data.Entities;

public class TreatmentPlan
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}
