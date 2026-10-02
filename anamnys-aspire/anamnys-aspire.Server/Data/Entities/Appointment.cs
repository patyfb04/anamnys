namespace Anamnys.Server.Data.Entities;

// Read-only column subset of "Appointments": the patient list needs only the next start
// time. Nothing writes appointments through EF yet, and the table has NOT NULL columns
// (EndsAt) this entity does not map, so an insert through it fails by design.
public class Appointment
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public string Status { get; set; } = "";
}
