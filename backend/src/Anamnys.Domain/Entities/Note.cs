using Anamnys.Domain.Enums;

namespace Anamnys.Domain.Entities;

public class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public NoteStatus Status { get; set; } = NoteStatus.Draft;
    public InputMode InputMode { get; set; }
    public string? RawTranscript { get; set; }
    public StructuredNote? StructuredContent { get; set; }
    public List<BillingCode> BillingCodes { get; set; } = new();
    public string? PriorAuthLetter { get; set; }
    public List<AuditEntry> AuditTrail { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SignedAt { get; set; }
    public Patient? Patient { get; set; }
}

public class StructuredNote
{
    public NoteFormat Format { get; set; }
    public Dictionary<string, string> Sections { get; set; } = new();
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class BillingCode
{
    public string CptCode { get; set; } = string.Empty;
    public string Icd10Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }   // 0–1
    public int DenialRiskScore { get; set; }       // 0–100
    public List<string> Modifiers { get; set; } = new();
    public List<string> MissingDocumentation { get; set; } = new();
}

public class AuditEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Action { get; set; } = string.Empty; // ai_draft | provider_edit | signed | exported
    public string? FieldChanged { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public Guid ActorId { get; set; }
}
