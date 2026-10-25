namespace Anamnys.Server.ExternalDocuments;

public enum SniffOutcome { Ok, Empty, UnsupportedType, TypeMismatch }

public sealed record SniffResult(SniffOutcome Outcome, string? MimeType = null);

// The file's type comes from its first bytes; the extension must agree with it
// (design/specs/2026-10-25-external-documents-design.md §3).
public static class FileTypeSniffer
{
    private static readonly byte[] Pdf = "%PDF-"u8.ToArray();
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static SniffResult Sniff(string fileName, ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            return new SniffResult(SniffOutcome.Empty);
        }
        string? mime;
        string[] extensions;
        if (content.StartsWith(Pdf))
        {
            (mime, extensions) = ("application/pdf", [".pdf"]);
        }
        else if (content.StartsWith(Jpeg))
        {
            (mime, extensions) = ("image/jpeg", [".jpg", ".jpeg"]);
        }
        else if (content.StartsWith(Png))
        {
            (mime, extensions) = ("image/png", [".png"]);
        }
        else
        {
            return new SniffResult(SniffOutcome.UnsupportedType);
        }
        return extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase)
            ? new SniffResult(SniffOutcome.Ok, mime)
            : new SniffResult(SniffOutcome.TypeMismatch);
    }
}
