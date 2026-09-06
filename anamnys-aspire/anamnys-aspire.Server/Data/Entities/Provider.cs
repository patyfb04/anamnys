namespace Anamnys.Server.Data.Entities;

public class Provider
{
    public Guid Id { get; set; }
    public Guid ExternalSubject { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Specialty { get; set; } = "MentalHealth";
    public string PreferredNoteFormat { get; set; } = "DAP";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
