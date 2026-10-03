namespace Anamnys.Server.Data.Entities;

// A provider's clinical record of one person. Always belongs to a provider; the person's
// portal login, if any, is a separate PatientAccount linked through AccountId
// (design/specs/2026-10-02-patient-accounts-design.md).
public class Patient
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }

    // The linked portal account, or null when the person has none (or it was deleted:
    // the FK is ON DELETE SET NULL, so the record always survives the credential).
    public Guid? AccountId { get; set; }

    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public string? PreferredLanguage { get; set; }
    public DateTimeOffset? LastVisit { get; set; }

    // Where the provider's scheduling notices go. Not unique and not a login: the same
    // person can be a patient of several providers, and typing an email here is not a
    // portal invitation (design/specs/2026-10-01-patient-records-design.md §8).
    public string? ContactEmail { get; set; }
    public string? Phone { get; set; }

    // Set when the provider archives the patient: hidden from the list, record kept (see
    // design/specs/2026-10-01-patient-records-design.md).
    public DateTimeOffset? ArchivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
