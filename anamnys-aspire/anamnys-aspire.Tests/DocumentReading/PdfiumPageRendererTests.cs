using System.Buffers.Binary;
using Anamnys.Server.DocumentReading;
using FluentAssertions;

namespace Anamnys.Tests.DocumentReading;

// The real PDFium (native, from the bblanchon packages) through PDFtoImage.
public class PdfiumPageRendererTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Theory]
    [InlineData(0, 150, 1240, 1754)]
    [InlineData(1, 72, 595, 842)]
    public void RenderPng_SamplePage_IsAPngOfThePageSizeAtThatResolution(int page, int dpi, int width, int height)
    {
        // Arrange
        var pdf = TestPdfs.Sample("vinicius-andrade-relatorio-psiquiatrico.pdf");

        // Act
        var png = new PdfiumPageRenderer().RenderPng(pdf, page, dpi);

        // Assert
        png.AsSpan(0, 8).ToArray().Should().Equal(PngSignature);
        var (w, h) = (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        w.Should().BeCloseTo(width, 2);
        h.Should().BeCloseTo(height, 2);
    }

    [Fact]
    public void RenderPng_PageOutOfRange_Throws()
    {
        // Arrange
        var pdf = TestPdfs.Sample("fernanda-barbosa-receita.pdf");

        // Act
        var act = () => new PdfiumPageRenderer().RenderPng(pdf, 5, 72);

        // Assert
        act.Should().Throw<Exception>();
    }
}
