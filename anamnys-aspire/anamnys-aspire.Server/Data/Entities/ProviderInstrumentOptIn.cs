namespace Anamnys.Server.Data.Entities;

public class ProviderInstrumentOptIn
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid InstrumentId { get; set; }
    public DateTimeOffset AcknowledgedAt { get; set; }
    public string ExplanationShown { get; set; } = "";
    public DateTimeOffset? RevokedAt { get; set; }
}
