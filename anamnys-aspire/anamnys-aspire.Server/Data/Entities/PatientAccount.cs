namespace Anamnys.Server.Data.Entities;

public class PatientAccount
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public Guid? ExternalSubject { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
