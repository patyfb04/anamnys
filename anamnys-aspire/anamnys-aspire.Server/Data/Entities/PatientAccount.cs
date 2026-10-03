namespace Anamnys.Server.Data.Entities;

// A patient-portal login, separate from any provider's clinical record by design: the
// record is clinical history kept for at least five years (Res. CFP 001/2009), the account
// is a credential. One account links to at most one record per provider through
// Patient.AccountId. Names, phone and date of birth here are what the person maintains in
// the portal; providers keep their own on the record, and the two are never synchronised
// (design/specs/2026-10-02-patient-accounts-design.md).
public class PatientAccount
{
    public Guid Id { get; set; }
    public Guid ExternalSubject { get; set; }
    public string Email { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Phone { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
