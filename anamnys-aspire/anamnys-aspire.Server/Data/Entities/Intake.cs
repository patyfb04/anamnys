namespace Anamnys.Server.Data.Entities;

// One intake of a patient (design/specs/2026-11-12-structured-intake-design.md §2): draft while answered, signed with
// its Anamnese note. At most one draft per patient (Intakes_OneDraft_key).
public class Intake
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public Guid TemplateId { get; set; }
    public Guid? NoteId { get; set; }
    public string Status { get; set; } = IntakeStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
}

public static class IntakeStatus
{
    public const string Draft = "draft";
    public const string Signed = "signed";
}
