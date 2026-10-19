namespace Anamnys.Server.Data.Entities;

// RawAnswers is deliberately unmapped: only the score and the date are stored.
public class ScaleApplication
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid InstrumentId { get; set; }
    public int Score { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public Guid? AppliedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public Guid? RemovedBy { get; set; }
}
