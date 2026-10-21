namespace Anamnys.Server.Data.Entities;

public class ConformityRuleset
{
    public Guid Id { get; set; }
    public string DocumentKind { get; set; } = "";
    public string Version { get; set; } = "";
    public List<string> Elements { get; set; } = [];
    public List<string> Prohibitions { get; set; } = [];
    public string SourceRef { get; set; } = "";
    public string? ArticleRef { get; set; }
    public DateOnly EffectiveFrom { get; set; }
}
