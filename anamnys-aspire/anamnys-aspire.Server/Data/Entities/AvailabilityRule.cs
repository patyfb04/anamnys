namespace Anamnys.Server.Data.Entities;

// One interval of one weekday in one version of a provider's weekly hours
// (design/specs/2026-11-09-availability-design.md §2, §3). Weekday 0 = Sunday. Times are in Timezone, the provider's
// zone. A version is the set of rows sharing EffectiveFrom; EffectiveTo null means open-ended. SlotMinutes is for F-62.
public class AvailabilityRule
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid VersionId { get; set; }
    public int Weekday { get; set; }
    public TimeOnly StartsAtTime { get; set; }
    public TimeOnly EndsAtTime { get; set; }
    public int SlotMinutes { get; set; } = 50;
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string Timezone { get; set; } = "America/Sao_Paulo";
}
