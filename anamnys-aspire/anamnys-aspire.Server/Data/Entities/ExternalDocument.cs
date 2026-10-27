namespace Anamnys.Server.Data.Entities;

// A file the patient brought (report, prescription, exam), uploaded by the provider and scanned for
// malware before it can be opened (design/specs/2026-10-25-external-documents-design.md §2).
// ScanStatus: pending | clean | infected | failed. ObjectKey is null once an infected file or a deleted
// document has no object left. Filename is for display only and never leaves the database.
public class ExternalDocument
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public string Filename { get; set; } = "";
    public string MimeType { get; set; } = "";
    public string? ObjectKey { get; set; }
    public long ByteSize { get; set; }
    public string ScanStatus { get; set; } = "pending";
    public DateTimeOffset UploadedAt { get; set; }
    public string Kind { get; set; } = "outro";
    public DateOnly? DocumentDate { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? ScannedAt { get; set; }
    public int ScanAttempts { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    // design/specs/2026-10-26-document-facts-design.md §2. ReadStatus: null until scanned clean, then
    // pending | reading | done | failed. PagesTruncated: the document had more pages than were read.
    public string? ReadStatus { get; set; }
    public int ReadAttempts { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public int? PageCount { get; set; }
    public bool PagesTruncated { get; set; }
    // Fact extractions started since the last read (counted before the model runs, reset by a new read
    // and by "Ler de novo"): the startup sweep does not re-queue a document that used them up.
    public int ExtractAttempts { get; set; }
}
