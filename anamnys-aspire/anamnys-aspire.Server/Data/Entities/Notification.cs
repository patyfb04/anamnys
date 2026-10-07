namespace Anamnys.Server.Data.Entities;

// Provider in-app notification (design/specs/2026-10-07-appointment-notifications-design.md §3).
public class Notification
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string Kind { get; set; } = "";
    public string? SubjectType { get; set; }
    public Guid? SubjectId { get; set; }
    public string Channel { get; set; } = "in_app";
    public DateTimeOffset ScheduledFor { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string DeliveryStatus { get; set; } = "sent";
    public DateTimeOffset? ReadAt { get; set; }
}
