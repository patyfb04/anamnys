using Anamnys.Server.DocumentReading;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Anamnys.Tests.DocumentReading;

public class DocumentReaderTests
{
    private const string Prose = "Paciente em acompanhamento psiquiátrico desde março.";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeRenderer : IPageRenderer
    {
        public List<(int Page, int Dpi)> Rendered { get; } = [];

        public byte[] RenderPng(byte[] pdf, int pageIndex, int dpi)
        {
            Rendered.Add((pageIndex, dpi));
            return [(byte)pageIndex];
        }
    }

    private sealed class FakeOcr(string text) : IOcrEngine
    {
        public List<byte[]> Images { get; } = [];

        public Task<string> RecognizeAsync(byte[] image, CancellationToken ct)
        {
            Images.Add(image);
            return Task.FromResult(text);
        }
    }

    private static DocumentReader Reader(IPageRenderer renderer, IOcrEngine ocr, int maxPages = 20, int maxRenderPixels = 4200) =>
        new(renderer, ocr, Options.Create(new DocumentReadingOptions { MaxPages = maxPages, MaxRenderPixels = maxRenderPixels }));

    private static byte[] SamplePng(int dpi) =>
        new PdfiumPageRenderer().RenderPng(TestPdfs.Sample("fernanda-barbosa-receita.pdf"), 0, dpi);

    private static (int Width, int Height) PngSize(byte[] png) =>
        (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)),
         System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

    [Fact]
    public async Task ReadAsync_PdfWithEmbeddedText_KeepsTheTextWithoutRenderingOrOcr()
    {
        // Arrange
        var renderer = new FakeRenderer();
        var ocr = new FakeOcr("nunca");
        var pdf = TestPdfs.WithPages(Prose, "Segunda página com texto suficiente para leitura.");

        // Act
        var result = await Reader(renderer, ocr).ReadAsync(pdf, "application/pdf", Ct);

        // Assert
        result.PageCount.Should().Be(2);
        result.Truncated.Should().BeFalse();
        result.Pages.Select(p => p.PageNumber).Should().Equal(1, 2);
        result.Pages.Should().OnlyContain(p => !p.FromOcr);
        result.Pages[0].Text.Should().Contain("acompanhamento psiquiátrico");
        renderer.Rendered.Should().BeEmpty();
        ocr.Images.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_PageWithoutText_IsRenderedAndOcrd_OnlyThatPage()
    {
        // Arrange
        var renderer = new FakeRenderer();
        var ocr = new FakeOcr("Texto lido pelo OCR da página digitalizada.\r\n");
        var pdf = TestPdfs.WithPages(Prose, null);

        // Act
        var result = await Reader(renderer, ocr).ReadAsync(pdf, "application/pdf", Ct);

        // Assert
        renderer.Rendered.Should().Equal((1, 300));
        result.Pages[0].FromOcr.Should().BeFalse();
        result.Pages[1].Should().Be(new ReadPage(2, "Texto lido pelo OCR da página digitalizada.", FromOcr: true));
    }

    [Fact]
    public async Task ReadAsync_ScanWhereOcrFindsLessThanTheEmbeddedText_KeepsTheEmbeddedText()
    {
        // Arrange
        var renderer = new FakeRenderer();
        var ocr = new FakeOcr("");
        var pdf = TestPdfs.WithPages("Pág. 3");

        // Act
        var result = await Reader(renderer, ocr).ReadAsync(pdf, "application/pdf", Ct);

        // Assert
        renderer.Rendered.Should().HaveCount(1);
        result.Pages.Should().ContainSingle().Which.Should().Be(new ReadPage(1, "Pág. 3", FromOcr: false));
    }

    [Fact]
    public async Task ReadAsync_MorePagesThanTheLimit_ReadsTheFirstOnes_AndFlagsTheRest()
    {
        // Arrange
        var renderer = new FakeRenderer();
        var pdf = TestPdfs.WithPages(Prose, Prose, Prose, Prose);

        // Act
        var result = await Reader(renderer, new FakeOcr(""), maxPages: 3).ReadAsync(pdf, "application/pdf", Ct);

        // Assert
        result.PageCount.Should().Be(4);
        result.Truncated.Should().BeTrue();
        result.Pages.Select(p => p.PageNumber).Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    public async Task ReadAsync_Image_GoesStraightToOcr_AsOnePage(string mimeType)
    {
        // Arrange
        var ocr = new FakeOcr("Receita: Sertralina 50 mg.");
        var image = SamplePng(72);

        // Act
        var result = await Reader(new FakeRenderer(), ocr).ReadAsync(image, mimeType, Ct);

        // Assert
        ocr.Images.Should().ContainSingle().Which.Should().BeSameAs(image);
        result.Should().BeEquivalentTo(new DocumentReadResult(1, false, [new ReadPage(1, "Receita: Sertralina 50 mg.", true)]));
    }

    [Fact]
    public async Task ReadAsync_ImageLargerThanTheRenderLimit_IsScaledDownBeforeOcr()
    {
        // Arrange
        var ocr = new FakeOcr("texto");
        var image = SamplePng(72);

        // Act
        await Reader(new FakeRenderer(), ocr, maxRenderPixels: 421).ReadAsync(image, "image/png", Ct);

        // Assert
        var (width, height) = PngSize(ocr.Images.Should().ContainSingle().Subject);
        PngSize(image).Should().Be((595, 842));
        height.Should().Be(421);
        width.Should().BeInRange(296, 298);
    }

    [Fact]
    public async Task ReadAsync_ImageThatDoesNotDecode_IsUnreadable()
    {
        // Arrange
        var ocr = new FakeOcr("nunca");

        // Act
        var act = () => Reader(new FakeRenderer(), ocr).ReadAsync([1, 2, 3], "image/jpeg", Ct);

        // Assert
        await act.Should().ThrowAsync<UnreadableDocumentException>();
        ocr.Images.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_MalformedPdf_IsUnreadable()
    {
        // Arrange
        var reader = Reader(new FakeRenderer(), new FakeOcr(""));

        // Act
        var act = () => reader.ReadAsync("%PDF-1.4 but nothing else"u8.ToArray(), "application/pdf", Ct);

        // Assert
        await act.Should().ThrowAsync<UnreadableDocumentException>();
    }

    [Fact]
    public async Task ReadAsync_OtherType_IsRefused()
    {
        // Arrange
        var reader = Reader(new FakeRenderer(), new FakeOcr(""));

        // Act
        var act = () => reader.ReadAsync([1], "text/plain", Ct);

        // Assert
        await act.Should().ThrowAsync<UnsupportedDocumentException>();
    }

    [Theory]
    [InlineData(595, 842, 300, 4200, 300)]
    [InlineData(842, 595, 300, 4200, 300)]
    [InlineData(2384, 3370, 300, 4200, 89)]
    [InlineData(20000, 20000, 300, 4200, 36)]
    [InlineData(0, 0, 300, 4200, 300)]
    [InlineData(595, 842, 20, 4200, 20)]
    [InlineData(595, 842, 0, 4200, 1)]
    [InlineData(595, 842, -5, 4200, 1)]
    [InlineData(595, 842, 300, 0, 300)]
    [InlineData(1e300, 1e300, 300, 4200, 36)]
    [InlineData(1e-300, 1e-300, 300, 4200, 300)]
    [InlineData(double.NaN, 842, 300, 4200, 300)]
    public void DpiFor_CapsTheLongestRenderedSide(double width, double height, int dpi, int maxPixels, int expected)
    {
        // Arrange
        // Act
        var result = DocumentReader.DpiFor(width, height, dpi, maxPixels);

        // Assert
        result.Should().Be(expected);
    }
}
