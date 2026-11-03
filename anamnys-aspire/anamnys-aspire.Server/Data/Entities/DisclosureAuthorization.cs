namespace Anamnys.Server.Data.Entities;

// The patient's authorization to share one document with one recipient for one purpose, as recorded by the provider
// (design/specs/2026-11-01-protected-sharing-design.md §2). TermObjectKey is the optional attached term, id-only key.
public class DisclosureAuthorization
{
    public const string VerbalSession = "verbal_session";
    public const string SignedTerm = "signed_term";
    public const string InWriting = "in_writing";

    public static readonly string[] ObtainedVias = [VerbalSession, SignedTerm, InWriting];

    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public string RecipientName { get; set; } = "";
    public string RecipientRole { get; set; } = "";
    public string Purpose { get; set; } = "";
    public DateTimeOffset AuthorizedAt { get; set; }
    public Guid RecordedBy { get; set; }
    public string ObtainedVia { get; set; } = "";
    public string? TermObjectKey { get; set; }
    public DateTimeOffset ConfirmedAt { get; set; }
}
