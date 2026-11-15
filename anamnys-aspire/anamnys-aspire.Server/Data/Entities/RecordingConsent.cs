namespace Anamnys.Server.Data.Entities;

// The patient's consent to session audio recording (design/specs/2026-11-15-recording-consent-design.md §2). One per
// patient in SENT or SIGNED at a time; the older rows stay as history. TreatmentPlanId stays null (per patient while in
// care). State: NOT_REQUESTED | SENT | SIGNED | REVOKED | EXPIRED (RecordingConsent/ConsentRules).
public class RecordingConsent
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? TreatmentPlanId { get; set; }
    public string State { get; set; } = "NOT_REQUESTED";
    public string? TemplateVersion { get; set; }
    // When the signing link was (last) sent.
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    // JSON summary: { "birthDateMatched": true, "termSha256": "...", "termVersion": "v1", "signerRole": "patient" }.
    public string? SignatureEvidence { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? GuardianName { get; set; }
    // The practice-local day the provider recorded the minor's assent (stored as that day's local midnight).
    public DateTimeOffset? AssentRecordedAt { get; set; }
    public string? ProviderNote { get; set; }
    // The rendered term: the text sent (date of birth and signature still open), then the signed text.
    public string? TermText { get; set; }
    public string? TermSha256 { get; set; }
    // patient | guardian
    public string? SignerRole { get; set; }
    public string? SignerName { get; set; }
    public string? GuardianRelationship { get; set; }
    public string? AssentNote { get; set; }
    public byte[]? SignTokenHash { get; set; }
    public DateTimeOffset? SignTokenExpiresAt { get; set; }
    public int FailedBirthDateCount { get; set; }
    public byte[]? RevokeTokenHash { get; set; }
    // patient | provider
    public string? RevokedBy { get; set; }
    public string? RevokeReason { get; set; }
    // link | discharge | adulthood | new_version
    public string? ExpiredReason { get; set; }
    public string? DocumentObjectKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// One state change (or a recorded assent, FromState = ToState). ActorKind: provider | signer | system; Reason is a code.
public class ConsentEvent
{
    public Guid Id { get; set; }
    public Guid ConsentId { get; set; }
    public string FromState { get; set; } = "";
    public string ToState { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public Guid? ActorId { get; set; }
    public string ActorKind { get; set; } = "system";
    public string? Reason { get; set; }
}
