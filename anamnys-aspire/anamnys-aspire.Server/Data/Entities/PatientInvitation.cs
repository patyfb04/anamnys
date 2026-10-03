namespace Anamnys.Server.Data.Entities;

// A single-use invitation for a provider's record to be linked to a patient-portal
// account (design/specs/2026-10-03-portal-invitation-design.md). Only the SHA-256 of the
// token is stored; the token itself exists only in the emailed link. Rows are kept after
// acceptance or revocation as the history of when access was granted.
public class PatientInvitation
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public string Email { get; set; } = "";
    public byte[] TokenHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedAccountId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
