namespace Anamnys.Server.Data.Entities;

// Read-only column subset of "Appointments": the provider's patient list needs the next
// start time; the patient portal shows the schedule fields below. Nothing writes
// appointments through EF yet; the table's other NOT NULL columns are unmapped, so an
// insert through this entity fails by design.
public class Appointment
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Timezone { get; set; } = "";
    public string Modality { get; set; } = "";
    public string Status { get; set; } = "";
}
