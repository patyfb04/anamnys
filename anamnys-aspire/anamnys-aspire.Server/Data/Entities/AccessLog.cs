namespace Anamnys.Server.Data.Entities;

public class AccessLog
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string ActorType { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Scope { get; set; } = "";
    public string? TicketRef { get; set; }
    public DateTimeOffset? AuthorizedAt { get; set; }
    public DateTimeOffset At { get; set; }
}
