namespace Anamnys.Server.Data.Entities;

public class ClinicalDocument
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public Guid? NoteId { get; set; }
    public string Kind { get; set; } = "";
    public string Body { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = [];
    public List<string> Confirmations { get; set; } = [];
    public List<TeamMember> Team { get; set; } = [];
    public Guid? RulesetId { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    public string? SignedByCrp { get; set; }
    public string? SignedCity { get; set; }
    public string? SignedByName { get; set; }
    public string? SignedPatientName { get; set; }
    public DateOnly? RetentionUntil { get; set; }
    public Guid? SupersededBy { get; set; }
    public string? PdfObjectKey { get; set; }
    public string? PdfSha256 { get; set; }
    public DateTimeOffset? ExportedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
