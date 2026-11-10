namespace Anamnys.Server.Data.Entities;

// A block of a provider's time: block, vacation or local holiday (design/specs/2026-11-09-availability-design.md §2).
// An all-day block is stored from local midnight to the next local midnight in the provider's zone. Reason is free
// text of at most 200 characters and never goes to logs or audit values.
public class AvailabilityException
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public bool AllDay { get; set; }
    public string Kind { get; set; } = "block";
    public string? Reason { get; set; }
}
