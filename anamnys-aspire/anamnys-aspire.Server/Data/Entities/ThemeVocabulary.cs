namespace Anamnys.Server.Data.Entities;

public class ThemeVocabulary
{
    public Guid Id { get; set; }
    public string Version { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public bool Active { get; set; }
}
