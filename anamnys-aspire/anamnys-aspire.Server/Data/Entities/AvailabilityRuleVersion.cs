namespace Anamnys.Server.Data.Entities;

// One version of a provider's weekly hours (design/specs/2026-11-09-availability-design.md §3): valid from EffectiveFrom
// to EffectiveTo (null = open-ended). Its rules (AvailabilityRules.VersionId) carry the same dates; a version where every
// day is "Não atendo" has no rules.
public class AvailabilityRuleVersion
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
