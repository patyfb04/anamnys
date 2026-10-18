namespace Anamnys.Server.Data.Entities;

// Column subset of "Notes": the patient list and detail page need status, format and dates;
// live session finalize writes the transcript and failure state
// (design/specs/2026-10-10-live-session-transcription-design.md §3).
public class Note
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? SessionId { get; set; }
    public string Status { get; set; } = "";
    public string InputMode { get; set; } = "";
    public string Format { get; set; } = "";
    public string? RawTranscript { get; set; }
    public Guid? RecordingId { get; set; }
    public string? FailureReason { get; set; }
    public string? StructureStatus { get; set; }
    public string? StructureFailure { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    public string? SignedByCrp { get; set; }
    public string? PdfObjectKey { get; set; }
    public string? PdfSha256 { get; set; }
    public DateTimeOffset? ExportedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
