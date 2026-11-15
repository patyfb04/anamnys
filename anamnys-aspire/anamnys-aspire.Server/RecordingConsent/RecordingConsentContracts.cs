namespace Anamnys.Server.RecordingConsents;

public sealed record SendConsentRequest(string? ProviderNote);

public sealed record ConsentAssentRequest(DateOnly? Date, string? Note);

public sealed record ConsentRevokeRequest(string? Reason);

public sealed record PublicConsentSignRequest(DateOnly? BirthDate, string? SignerName, string? Relationship, bool Accepted);

public sealed record PublicConsentRevokeRequest(DateOnly? BirthDate);

public sealed record ConsentEventView(string FromState, string ToState, DateTimeOffset At, string ActorKind, string? Reason);

// GET patients/{patientId}/recording-consent. State is "none" when the patient never had a consent (the consent fields
// then null). SendBlockedBy names the first sending precondition that fails (profile_incomplete, patient_email_missing,
// patient_birthdate_missing, patient_archived), null when a new term can be sent. AssentRecordedAt is the practice day.
public sealed record ConsentView(
    Guid? Id,
    string State,
    string? Version,
    string CurrentVersion,
    DateTimeOffset? SentAt,
    DateTimeOffset? SignedAt,
    string? SignerRole,
    string? SignerName,
    string? GuardianRelationship,
    DateOnly? AssentRecordedAt,
    string? AssentNote,
    DateTimeOffset? RevokedAt,
    string? RevokedBy,
    string? RevokeReason,
    string? ExpiredReason,
    DateTimeOffset? LinkExpiresAt,
    string? ProviderNote,
    bool HasDocument,
    bool IsMinor,
    bool CanRecord,
    string Reason,
    string? SendBlockedBy,
    IReadOnlyList<ConsentEventView> Events);

// GET patients/{patientId}/recording-consent/preview: the term as it would be sent, without the provider's observations
// (the UI shows the typed observations as the term's last paragraph, "Observações do profissional").
public sealed record ConsentPreview(string Version, string Text, string? SendBlockedBy);

// The public signing page: the term (without the date of birth) and whether a guardian signs.
public sealed record PublicConsentView(string Text, bool GuardianExpected, string ProviderName);

// The public revocation page: the signed term.
public sealed record PublicRevokeView(string Text, DateTimeOffset SignedAt, string SignerName, string TermSha256, string ProviderName);
