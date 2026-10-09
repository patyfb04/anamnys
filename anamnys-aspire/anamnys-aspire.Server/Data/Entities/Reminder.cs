namespace Anamnys.Server.Data.Entities;

// Patient e-mail outbox (design/specs/2026-10-07-appointment-notifications-design.md §3).
public class Reminder
{
    public Guid Id { get; set; }
    public Guid AppointmentId { get; set; }
    public string Channel { get; set; } = "email";
    public string TemplateKey { get; set; } = "";
    public DateTimeOffset ScheduledFor { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string DeliveryStatus { get; set; } = "pending";
    public int Attempts { get; set; }
    public Guid? ConfirmationId { get; set; }
}
