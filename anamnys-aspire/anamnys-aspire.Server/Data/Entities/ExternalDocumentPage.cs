namespace Anamnys.Server.Data.Entities;

// The text read from one page of an external document (design/specs/2026-10-26-document-facts-design.md §2):
// the embedded PDF text, or the OCR result when the page is a scan or the document is an image (FromOcr).
public class ExternalDocumentPage
{
    public Guid Id { get; set; }
    public Guid ExternalDocumentId { get; set; }
    public Guid ProviderId { get; set; }
    public int PageNumber { get; set; }
    public string Text { get; set; } = "";
    public bool FromOcr { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
