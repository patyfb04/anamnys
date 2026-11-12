namespace Anamnys.Server.Data.Entities;

// A recurring appointment (design/specs/2026-11-09-appointment-series-design.md §2). Occurrence k (1-based) falls on
// StartsOn + (k - 1) * 7 * Interval days at StartTime in Timezone; occurrences are real Appointments rows. The series
// ends never, on EndsOn, or after OccurrenceCount sessions (EndsOn and OccurrenceCount are never both set).
public class AppointmentSeries
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public string RecurrenceRule { get; set; } = "";
    public DateOnly StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    public int DefaultMinutes { get; set; }
    public string DefaultModality { get; set; } = "";
    public DateTimeOffset? CancelledAt { get; set; }
    public int Interval { get; set; } = 1;
    public int Weekday { get; set; }
    public TimeOnly StartTime { get; set; }
    public string Timezone { get; set; } = "America/Sao_Paulo";
    public int? OccurrenceCount { get; set; }
    public DateOnly? GeneratedUntil { get; set; }
    public Guid? PreviousSeriesId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
