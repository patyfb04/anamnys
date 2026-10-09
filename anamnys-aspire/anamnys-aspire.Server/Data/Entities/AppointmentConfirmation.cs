namespace Anamnys.Server.Data.Entities;

// Confirmation of one appointment (design/specs/2026-10-07-appointment-notifications-design.md §3).
public class AppointmentConfirmation
{
    public Guid Id { get; set; }
    public Guid AppointmentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DeadlineAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? ConfirmedBy { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}
