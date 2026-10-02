namespace Anamnys.Server.Data.Entities;

public class Patient
{
    public Guid Id { get; set; }
    public Guid? ProviderId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public string? PreferredLanguage { get; set; }
    public DateTimeOffset? LastVisit { get; set; }

    // Everything below used to live on the separate PatientAccounts table — a patient's
    // portal login is now just nullable columns on their clinical record, the same shape
    // Provider already uses. Null on all of them means a provider created this patient
    // with no portal invite; Email + ExternalSubject both set means a bound, logged-in
    // patient.
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public Guid? ExternalSubject { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }

    // Set when the provider archives the patient: hidden from the list, record kept (see
    // design/specs/2026-10-01-patient-records-design.md).
    public DateTimeOffset? ArchivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
