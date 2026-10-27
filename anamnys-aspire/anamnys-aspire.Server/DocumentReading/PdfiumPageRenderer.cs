using PDFtoImage;

namespace Anamnys.Server.DocumentReading;

public interface IPageRenderer
{
    // PNG of one page (0-based index) at the given resolution.
    byte[] RenderPng(byte[] pdf, int pageIndex, int dpi);
}

// PDFium (bblanchon native builds) through PDFtoImage, in process.
public sealed class PdfiumPageRenderer : IPageRenderer
{
    public byte[] RenderPng(byte[] pdf, int pageIndex, int dpi)
    {
        using var output = new MemoryStream();
        Conversion.SavePng(output, pdf, pageIndex, password: null,
            options: new RenderOptions(Dpi: dpi, WithAnnotations: true, BackgroundColor: SkiaSharp.SKColors.White, Grayscale: true));
        return output.ToArray();
    }
}
