namespace Anamnys.Server.Data.Entities;

public class ThemeTerm
{
    public Guid Id { get; set; }
    public Guid VocabularyId { get; set; }
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public Guid? ParentTermId { get; set; }
    public DateOnly IntroducedOn { get; set; }
    public string Status { get; set; } = "active";
    public Guid? SupersededByTermId { get; set; }
}
