namespace Anamnys.Server.Data.Entities;

// A fact an external document declares (design/specs/2026-10-26-document-facts-design.md §2).
// Kind: issuer | registration | issue_date | icd | medication | conclusion. Status: pending | confirmed |
// corrected | discarded; only confirmed and corrected facts count, and a correction keeps Value (what was
// read) next to CorrectedValue. Quote is the sentence on page PageRef the fact was read from.
public class ExtractedFact
{
    public Guid Id { get; set; }
    public Guid ExternalDocumentId { get; set; }
    public Guid ProviderId { get; set; }
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public string? CorrectedValue { get; set; }
    public int? PageRef { get; set; }
    public Guid? ExcerptRefId { get; set; }
    public string Status { get; set; } = "pending";
    public string? Quote { get; set; }
    public Guid? ConfirmedBy { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DiscardedAt { get; set; }
}
