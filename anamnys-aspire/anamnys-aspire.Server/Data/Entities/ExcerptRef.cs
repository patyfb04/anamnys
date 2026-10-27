namespace Anamnys.Server.Data.Entities;

// Where a derived item came from (design/specs/2026-10-24-excerpt-references-design.md §2).
// Kind "exact": OffsetStart/End delimit Quote inside the section text; "section": the whole section.
// SourceType "document": SourceId is the external document and the offsets point into page PageNumber.
public class ExcerptRef
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string SourceType { get; set; } = "note";
    public Guid SourceId { get; set; }
    public string? SectionKey { get; set; }
    public int? PageNumber { get; set; }
    public string Kind { get; set; } = "section";
    public int? OffsetStart { get; set; }
    public int? OffsetEnd { get; set; }
    public string? Quote { get; set; }
    public DateTimeOffset DatedAt { get; set; }
}
