using Anamnys.Server.DocumentReading;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Anamnys.Tests.DocumentReading;

// The real Tesseract with the pinned por.traineddata from the server's appsettings.json (downloaded on
// first use to the default directory, so later runs reuse it). Skipped when the data cannot be fetched.
public sealed class DocumentReadingOcrTests : IDisposable
{
    private const string Sentence = "Paciente apresenta evolução clínica favorável\ncom melhora do sono e da ansiedade.";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IOptions<DocumentReadingOptions> _options = Options.Create(
        new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).Build()
            .GetSection("DocumentReading").Get<DocumentReadingOptions>() ?? new DocumentReadingOptions());

    private readonly TesseractOcr _ocr;

    public DocumentReadingOcrTests()
    {
        _ocr = new TesseractOcr(new TessdataProvider(_options, new DefaultFactory(), NullLogger<TessdataProvider>.Instance));
    }

    public void Dispose() => _ocr.Dispose();

    private sealed class DefaultFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromMinutes(5) };
    }

    private async Task RequireTessdataAsync()
    {
        try
        {
            await new TessdataProvider(_options, new DefaultFactory(), NullLogger<TessdataProvider>.Instance).GetDirectoryAsync(Ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            Assert.Skip($"Tesseract data is not available: {e.GetType().Name}");
        }
    }

    private static void ShouldReadTheSentence(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (var word in new[] { "paciente", "apresenta", "evolução", "clínica", "favorável", "melhora", "sono", "ansiedade" })
        {
            lower.Should().Contain(word);
        }
    }

    [Fact]
    public async Task RecognizeAsync_RenderedPortugueseSentence_IsReadWithItsAccents()
    {
        // Arrange
        await RequireTessdataAsync();
        var png = new PdfiumPageRenderer().RenderPng(TestPdfs.WithPages(Sentence), 0, 300);

        // Act
        var text = await _ocr.RecognizeAsync(png, Ct);

        // Assert
        ShouldReadTheSentence(text);
    }

    [Fact]
    public async Task DocumentReader_ScannedPdfPage_IsClassifiedRenderedAndOcrd()
    {
        // Arrange
        await RequireTessdataAsync();
        var scan = TestPdfs.ScanOf(new PdfiumPageRenderer().RenderPng(TestPdfs.WithPages(Sentence), 0, 200));
        var reader = new DocumentReader(new PdfiumPageRenderer(), _ocr, _options);

        // Act
        var result = await reader.ReadAsync(scan, "application/pdf", Ct);

        // Assert
        PageText.NeedsOcr(PdfTextReader.Read(scan, 1, Ct).Pages[0].Text, 20).Should().BeTrue();
        var page = result.Pages.Should().ContainSingle().Which;
        page.FromOcr.Should().BeTrue();
        ShouldReadTheSentence(page.Text);
    }

    [Fact]
    public async Task DocumentReader_PngImage_IsOcrdAsOnePage()
    {
        // Arrange
        await RequireTessdataAsync();
        var png = new PdfiumPageRenderer().RenderPng(TestPdfs.WithPages(Sentence), 0, 300);
        var reader = new DocumentReader(new PdfiumPageRenderer(), _ocr, _options);

        // Act
        var result = await reader.ReadAsync(png, "image/png", Ct);

        // Assert
        var page = result.Pages.Should().ContainSingle().Which;
        page.FromOcr.Should().BeTrue();
        ShouldReadTheSentence(page.Text);
    }
}
