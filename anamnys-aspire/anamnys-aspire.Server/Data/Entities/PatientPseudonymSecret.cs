namespace Anamnys.Server.Data.Entities;

// F-27: the patient's 32-byte pseudonym secret, protected with ASP.NET Core Data Protection (purpose
// Anamnys.Pseudonymization.v1). Deleted with the patient's clinical content on a discard.
public class PatientPseudonymSecret
{
    public Guid PatientId { get; set; }
    public byte[] Protected { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}
