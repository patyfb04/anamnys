namespace Anamnys.Server.Data.Entities;

// One request for a full account export (design/specs/2026-10-23-account-export-design.md §3).
// Status: queued | running | done | failed | expired.
public class AccountExport
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ObjectKey { get; set; }
    public string Format { get; set; } = "zip";
    public string Scope { get; set; } = "full";
    public string Status { get; set; } = "queued";
    public string? FailureReason { get; set; }
    public long? SizeBytes { get; set; }
    public DateTimeOffset? ExpiredAt { get; set; }
}
