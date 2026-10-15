namespace Anamnys.Server.Data.Entities;

// Column subset of "Sessions": what happened, as opposed to the appointment (what was planned).
// Created by live session finalize (design/specs/2026-10-10-live-session-transcription-design.md §5).
public class Session
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public DateTimeOffset? ScheduledStart { get; set; }
    public int? ScheduledMinutes { get; set; }
    public DateTimeOffset? ActualStart { get; set; }
    public DateTimeOffset? ActualEnd { get; set; }
    public string? Modality { get; set; }
    public string Attendance { get; set; } = "scheduled";
    public DateTimeOffset CreatedAt { get; set; }
}
