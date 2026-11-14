namespace Anamnys.Server.Data.Entities;

// An intake questionnaire (design/specs/2026-11-12-structured-intake-design.md §2). The system default has no provider
// and IsDefault; a provider's own templates are copies (BasedOnTemplateId). Version is text in the schema ("1", "2", ...);
// editing a template already used by an intake archives it and creates the next version.
public class IntakeTemplate
{
    public Guid Id { get; set; }
    public Guid? ProviderId { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1";
    public bool Active { get; set; } = true;
    public bool IsDefault { get; set; }
    public Guid? BasedOnTemplateId { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
