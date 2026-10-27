using Anamnys.Server.DocumentReading;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Anamnys.Tests.DocumentReading;

// The real PdfPig against the dev-seed sample documents (dev-seed/make-sample-documents.mjs), which
// carry embedded WinAnsi text.
public class PdfTextReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Samples() => new(TestPdfs.SampleNames());

    [Theory]
    [MemberData(nameof(Samples))]
    public void Read_SampleDocument_EveryPageHasEnoughEmbeddedTextToSkipOcr(string name)
    {
        // Arrange
        var pdf = TestPdfs.Sample(name);

        // Act
        var result = PdfTextReader.Read(pdf, 20, Ct);

        // Assert
        result.PageCount.Should().BeGreaterThan(0);
        result.Pages.Should().HaveCount(result.PageCount);
        result.Pages.Should().OnlyContain(p => !PageText.NeedsOcr(PageText.Normalize(p.Text), 20));
        result.Pages.Should().OnlyContain(p => p.WidthPoints == 595 && p.HeightPoints == 842);
    }

    [Fact]
    public void Read_TwoPageReport_KeepsAccentsLinesAndPageOrder()
    {
        // Arrange
        var pdf = TestPdfs.Sample("vinicius-andrade-relatorio-psiquiatrico.pdf");

        // Act
        var result = PdfTextReader.Read(pdf, 20, Ct);

        // Assert
        result.PageCount.Should().Be(2);
        var first = PageText.Normalize(result.Pages[0].Text);
        var second = PageText.Normalize(result.Pages[1].Text);
        first.Should().Contain("RELATÓRIO MÉDICO PSIQUIÁTRICO");
        first.Should().Contain("CID-10 F33.1 - Transtorno depressivo recorrente, episódio atual moderado.");
        first.Split('\n').Should().Contain("Paciente: Vinícius Andrade");
        second.Should().Contain("Bupropiona 150 mg, 1 comprimido pela manhã (início em 10/03/2026).");
        second.Should().Contain("Psiquiatra - CRM-SP 333333 (fictício)");
        first.Should().NotContain("Bupropiona");
    }

    [Fact]
    public async Task DocumentReader_SampleDocuments_AreReadWithoutOcr()
    {
        // Arrange
        var reader = new DocumentReader(new ThrowingRenderer(), new ThrowingOcr(), Options.Create(new DocumentReadingOptions()));

        // Act
        var results = new List<DocumentReadResult>();
        foreach (var name in TestPdfs.SampleNames())
        {
            results.Add(await reader.ReadAsync(TestPdfs.Sample(name), "application/pdf", Ct));
        }

        // Assert
        results.Should().HaveCount(6);
        results.SelectMany(r => r.Pages).Should().OnlyContain(p => !p.FromOcr && p.Text.Length > 0);
    }

    [Fact]
    public void Read_NotAPdf_Throws()
    {
        // Arrange
        var bytes = "%PDF-1.4 but nothing else"u8.ToArray();

        // Act
        var act = () => PdfTextReader.Read(bytes, 20, Ct);

        // Assert
        act.Should().Throw<Exception>();
    }

    private sealed class ThrowingRenderer : IPageRenderer
    {
        public byte[] RenderPng(byte[] pdf, int pageIndex, int dpi) => throw new InvalidOperationException("no render expected");
    }

    private sealed class ThrowingOcr : IOcrEngine
    {
        public Task<string> RecognizeAsync(byte[] image, CancellationToken ct) => throw new InvalidOperationException("no OCR expected");
    }
}
