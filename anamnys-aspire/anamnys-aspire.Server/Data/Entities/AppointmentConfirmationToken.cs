namespace Anamnys.Server.Data.Entities;

// SHA-256 hash of one confirmation link (design/specs/2026-10-07-appointment-notifications-design.md §3).
public class AppointmentConfirmationToken
{
    public Guid Id { get; set; }
    public Guid ConfirmationId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}
