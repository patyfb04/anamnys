namespace Anamnys.Server.Data.Entities;

// One answer of an intake, unique per (IntakeId, QuestionId). PatientId and SessionId are kept from the original
// schema; PatientId is the intake's patient and SessionId stays null.
public class IntakeResponse
{
    public Guid Id { get; set; }
    public Guid IntakeId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid QuestionId { get; set; }
    public string? Answer { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
