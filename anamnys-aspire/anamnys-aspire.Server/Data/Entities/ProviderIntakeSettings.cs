namespace Anamnys.Server.Data.Entities;

// The provider's preferred intake template (design/specs/2026-11-12-structured-intake-design.md §2). No row means the
// system default.
public class ProviderIntakeSettings
{
    public Guid ProviderId { get; set; }
    public Guid DefaultTemplateId { get; set; }
}
