using Microsoft.Extensions.Options;

namespace Anamnys.Server.DocumentReading;

public sealed record ReadPage(int PageNumber, string Text, bool FromOcr);

// PageCount is the document's page count; Truncated when it has more pages than were read.
public sealed record DocumentReadResult(int PageCount, bool Truncated, IReadOnlyList<ReadPage> Pages);

public sealed class UnsupportedDocumentException(string mimeType)
    : Exception($"Documents of type {mimeType} are not read.");

// The file cannot be read as what it claims to be (a malformed or encrypted PDF, an image that does not
// decode): permanent, another attempt would fail the same way.
public sealed class UnreadableDocumentException(Exception? inner = null)
    : Exception("The document cannot be read.", inner);

public interface IDocumentReader
{
    Task<DocumentReadResult> ReadAsync(byte[] content, string mimeType, CancellationToken ct);
}

// Decides per page: the embedded PDF text when it has enough useful characters, otherwise the page is
// rendered and OCR'd (and the OCR text kept when it has more useful characters). JPEG and PNG go straight
// to OCR. Everything runs in process; nothing leaves the server.
public sealed class DocumentReader(IPageRenderer renderer, IOcrEngine ocr, IOptions<DocumentReadingOptions> options) : IDocumentReader
{
    public async Task<DocumentReadResult> ReadAsync(byte[] content, string mimeType, CancellationToken ct)
    {
        switch (mimeType)
        {
            case "application/pdf":
                return await ReadPdfAsync(content, ct);
            case "image/jpeg" or "image/png":
                var image = await Task.Run(() => ImageScaler.FitWithin(content, options.Value.MaxRenderPixels), ct);
                var text = PageText.Normalize(await ocr.RecognizeAsync(image, ct));
                return new DocumentReadResult(1, false, [new ReadPage(1, text, FromOcr: true)]);
            default:
                throw new UnsupportedDocumentException(mimeType);
        }
    }

    private async Task<DocumentReadResult> ReadPdfAsync(byte[] content, CancellationToken ct)
    {
        var o = options.Value;
        var maxPages = Math.Max(1, o.MaxPages);
        PdfText pdf;
        try
        {
            pdf = await Task.Run(() => PdfTextReader.Read(content, maxPages, ct), ct);
        }
        catch (Exception e) when (e is UglyToad.PdfPig.Core.PdfDocumentFormatException or UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
        {
            throw new UnreadableDocumentException(e);
        }
        var pages = new List<ReadPage>(pdf.Pages.Count);
        foreach (var page in pdf.Pages)
        {
            ct.ThrowIfCancellationRequested();
            var embedded = PageText.Normalize(page.Text);
            if (!PageText.NeedsOcr(embedded, o.MinTextCharacters))
            {
                pages.Add(new ReadPage(page.PageNumber, embedded, FromOcr: false));
                continue;
            }
            var dpi = DpiFor(page.WidthPoints, page.HeightPoints, o.RenderDpi, o.MaxRenderPixels);
            var image = await Task.Run(() => renderer.RenderPng(content, page.PageNumber - 1, dpi), ct);
            var recognized = PageText.Normalize(await ocr.RecognizeAsync(image, ct));
            pages.Add(PageText.UsefulCharacters(recognized) > PageText.UsefulCharacters(embedded)
                ? new ReadPage(page.PageNumber, recognized, FromOcr: true)
                : new ReadPage(page.PageNumber, embedded, FromOcr: false));
        }
        return new DocumentReadResult(pdf.PageCount, pdf.PageCount > maxPages, pages);
    }

    // The configured DPI, lowered so the longest side stays within maxPixels (72 points per inch), never
    // below 36 DPI (or the configured DPI when that is lower) and never below 1. Never throws.
    internal static int DpiFor(double widthPoints, double heightPoints, int dpi, int maxPixels)
    {
        var ceiling = Math.Max(1, dpi);
        var longest = Math.Max(widthPoints, heightPoints);
        if (!(longest > 0) || double.IsInfinity(longest) || maxPixels <= 0)
        {
            return ceiling;
        }
        var cap = Math.Floor(Math.Min(ceiling, maxPixels * 72.0 / longest));
        return Math.Max(Math.Min(36, ceiling), (int)cap);
    }
}
