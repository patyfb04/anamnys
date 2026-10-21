namespace Anamnys.Server.Data.Entities;

public class ConformityCheck
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid RulesetId { get; set; }
    public List<string> MissingElements { get; set; } = [];
    public List<string> ForbiddenFound { get; set; } = [];
    public string Mode { get; set; } = "advisory";
    public bool Passed { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
}
