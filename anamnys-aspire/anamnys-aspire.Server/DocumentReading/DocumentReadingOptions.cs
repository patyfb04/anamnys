namespace Anamnys.Server.DocumentReading;

// design/specs/2026-10-26-document-facts-design.md §1 and §3. The Tesseract Portuguese data is a public
// file fetched on first use and verified against TessdataSha256; without the hash nothing is downloaded.
public sealed class DocumentReadingOptions
{
    public string TessdataDirectory { get; set; } = "";
    public Uri TessdataUrl { get; set; } = new("https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/por.traineddata");
    public string TessdataSha256 { get; set; } = "";

    // Pages beyond this are not read; the document is flagged PagesTruncated.
    public int MaxPages { get; set; } = 20;
    // A PDF page with fewer letters and digits than this in its embedded text is treated as a scan.
    public int MinTextCharacters { get; set; } = 20;
    public int RenderDpi { get; set; } = 300;
    // Longest side, in pixels, of a page rendered for OCR: oversized pages are rendered at a lower DPI.
    public int MaxRenderPixels { get; set; } = 4200;

    public string ResolvedTessdataDirectory => TessdataDirectory is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "anamnys", "tessdata");
}
