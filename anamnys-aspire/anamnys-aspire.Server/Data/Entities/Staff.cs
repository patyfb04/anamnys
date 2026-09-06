namespace Anamnys.Server.Data.Entities;

public class Staff
{
    public Guid Id { get; set; }
    public Guid ExternalSubject { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "support";
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
