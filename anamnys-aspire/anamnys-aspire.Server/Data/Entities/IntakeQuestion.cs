namespace Anamnys.Server.Data.Entities;

// One question of an intake template (design/specs/2026-11-12-structured-intake-design.md §2). Sections are the
// questions' SectionKey/SectionTitle in Position order. TargetField is where a confirmed answer goes in the record.
public class IntakeQuestion
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public string SectionKey { get; set; } = "";
    public string SectionTitle { get; set; } = "";
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? HelpText { get; set; }
    public string AnswerType { get; set; } = "text";
    public string[]? Choices { get; set; }
    public int Position { get; set; }
    public string? TargetField { get; set; }
    public bool Required { get; set; }
    public bool Disabled { get; set; }
}
