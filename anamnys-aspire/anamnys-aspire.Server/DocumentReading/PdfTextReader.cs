using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Anamnys.Server.DocumentReading;

public sealed record PdfPageText(int PageNumber, string Text, double WidthPoints, double HeightPoints);

public sealed record PdfText(int PageCount, IReadOnlyList<PdfPageText> Pages);

// The embedded text of the first maxPages pages (PdfPig), in reading order.
public static class PdfTextReader
{
    public static PdfText Read(byte[] pdf, int maxPages, CancellationToken ct)
    {
        using var document = PdfDocument.Open(pdf);
        var count = document.NumberOfPages;
        var pages = new List<PdfPageText>(Math.Min(count, maxPages));
        for (var number = 1; number <= Math.Min(count, maxPages); number++)
        {
            ct.ThrowIfCancellationRequested();
            var page = document.GetPage(number);
            pages.Add(new PdfPageText(number, ContentOrderTextExtractor.GetText(page), page.Width, page.Height));
        }
        return new PdfText(count, pages);
    }
}
