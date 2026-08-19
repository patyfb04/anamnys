namespace Anamnys.Domain.Entities;

public class Patient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProviderId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public List<string> Diagnoses { get; set; } = new();
    public List<string> CurrentMedications { get; set; } = new();
    public string? TreatmentPlan { get; set; }

    /// <summary>
    /// ISO 639-1 code (e.g. "en", "pt") used as the default Whisper transcription language
    /// for this patient's notes. Null means auto-detect per recording.
    /// </summary>
    public string? PreferredLanguage { get; set; }

    public DateTimeOffset? LastVisit { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<Note> Notes { get; set; } = new List<Note>();
}
