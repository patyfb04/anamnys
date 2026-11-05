namespace Anamnys.Server.Data.Entities;

// The chain head of a provider, timestamped by an RFC 3161 authority (design/specs/2026-11-03-integrity-chain-design.md
// §2, §6). Token is the full TimeStampToken (DER); AnchoredAt is the genTime inside it. Append-only in the database.
public class IntegrityAnchor
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public long Sequence { get; set; }
    public string HeadHash { get; set; } = "";
    public string TsaUrl { get; set; } = "";
    public byte[] Token { get; set; } = [];
    public DateTimeOffset AnchoredAt { get; set; }
}
