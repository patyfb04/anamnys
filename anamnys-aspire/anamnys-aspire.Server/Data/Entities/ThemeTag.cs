namespace Anamnys.Server.Data.Entities;

public class ThemeTag
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public Guid TermId { get; set; }
    public Guid? ExcerptRefId { get; set; }
    public decimal? Confidence { get; set; }
    public string CreatedBy { get; set; } = "ai";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public Guid? AddedById { get; set; }
    public Guid? RemovedById { get; set; }
}
