namespace Anamnys.Server.Data.Entities;

// TermId and LastRecordedSessionId (theme vocabulary and session tracking) are unmapped
// until those features exist; both columns are nullable.
public class PlanObjective
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
