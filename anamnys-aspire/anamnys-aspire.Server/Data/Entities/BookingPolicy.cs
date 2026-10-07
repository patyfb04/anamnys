namespace Anamnys.Server.Data.Entities;

// Only the auto-cancel columns (design/specs/2026-10-07-appointment-notifications-design.md §3);
// the table's other columns keep their defaults on insert.
public class BookingPolicy
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string AutoCancelMode { get; set; } = "after_email";
    public int AutoCancelHours { get; set; } = 1;
}
