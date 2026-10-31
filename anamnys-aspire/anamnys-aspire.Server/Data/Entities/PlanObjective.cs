namespace Anamnys.Server.Data.Entities;

// TermId links the objective to a theme (F-14, design/specs/2026-10-30-plan-adherence-design.md); the adherence
// is computed from the signed notes, so LastRecordedSessionId stays unmapped (nullable, never written).
public class PlanObjective
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public string Description { get; set; } = "";
    public Guid? TermId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
