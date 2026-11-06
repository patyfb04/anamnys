namespace Anamnys.Server.Data.Entities;

// One patient dossier request and what was delivered (design/specs/2026-11-04-patient-dossier-design.md §2).
// Status: queued | running | done | failed | expired. The row outlives the ZIP: it proves what was delivered.
public class Dossier
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public int Number { get; set; }
    public string Status { get; set; } = "queued";
    // crp | court | patient_request | other; ReasonNote only for other, never audited.
    public string Reason { get; set; } = "";
    public string? ReasonNote { get; set; }
    public DateOnly? PeriodFrom { get; set; }
    public DateOnly? PeriodTo { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? ExpiredAt { get; set; }
    public string? ObjectKey { get; set; }
    public long? SizeBytes { get; set; }
    public string? ZipSha256 { get; set; }
    // The content of manifesto.json.
    public string? Manifest { get; set; }
    // The RFC 3161 token over ZipSha256; null when the authority failed at generation.
    public byte[]? AnchorToken { get; set; }
    public DateTimeOffset? AnchoredAt { get; set; }
    public bool IntegrityVerified { get; set; }
    // dossier_error | interrupted
    public string? FailureReason { get; set; }
}
