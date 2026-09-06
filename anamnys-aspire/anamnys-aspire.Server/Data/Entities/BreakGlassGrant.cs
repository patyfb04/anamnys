namespace Anamnys.Server.Data.Entities;

public class BreakGlassGrant
{
    public Guid Id { get; set; }
    public Guid StaffId { get; set; }
    public Guid ProviderId { get; set; }
    public string TicketRef { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid AuthorizedBy { get; set; }
    public DateTimeOffset AuthorizedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
