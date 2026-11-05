namespace Anamnys.Server.Data.Entities;

// One link of a provider's append-only hash chain (design/specs/2026-11-03-integrity-chain-design.md §2). Ids and hashes
// only, never clinical text: the seal outlives the discarded record, so SubjectId has no foreign key. The database
// refuses any change to a seal except DiscardedAt going from null to a value.
public class IntegritySeal
{
    public const string Note = "note";
    public const string Document = "document";

    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public long Sequence { get; set; }
    public string SubjectType { get; set; } = "";
    public Guid SubjectId { get; set; }
    public string ContentHash { get; set; } = "";
    public string? PreviousHash { get; set; }
    public string Hash { get; set; } = "";
    public DateTimeOffset SealedAt { get; set; }
    public bool Retroactive { get; set; }
    public DateTimeOffset? DiscardedAt { get; set; }
}
