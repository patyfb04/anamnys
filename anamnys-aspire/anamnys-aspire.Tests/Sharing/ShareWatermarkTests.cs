using Anamnys.Server.Documents;
using FluentAssertions;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Anamnys.Tests.Sharing;

public class ShareWatermarkTests
{
    private static readonly DateTimeOffset Opened = new(2026, 11, 1, 15, 30, 0, TimeSpan.Zero);

    static ShareWatermarkTests() => QuestPDF.Settings.License = LicenseType.Community;

    private static DocumentPdfModel LongModel() => new(
        "Relatório", "Dra. Paula Souza", "06/12345", "Ana Souza", null, [],
        [.. Enumerable.Range(0, 12).Select(i => new DocumentPdfSection($"Seção {i}", string.Join(' ', Enumerable.Repeat("Texto clínico da seção.", 120))))],
        "São Paulo", Opened, "A1B2C3D4");

    private static List<string> PageTexts(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return document.GetPages().Select(p => p.Text.Replace(" ", "")).ToList();
    }

    [Fact]
    public void Render_WithWatermark_ShowsRecipientAndPracticeTimeOnEveryPage()
    {
        // Act
        var pages = PageTexts(DocumentPdf.Render(LongModel(), new DocumentWatermark("Dr. Pedro Lima", Opened)));

        // Assert
        pages.Should().HaveCountGreaterThan(1);
        pages.Should().OnlyContain(p => p.Contains("Dr.PedroLima") && p.Contains("01/11/202612:30"));
    }

    [Fact]
    public void Render_WithWatermark_DrawsItAcrossTheMiddleOfThePage()
    {
        // Act
        using var pdf = PdfDocument.Open(DocumentPdf.Render(LongModel(), new DocumentWatermark("Dr. Pedro Lima", Opened)));

        // Assert
        var page = pdf.GetPage(1);
        var drawn = page.Letters.Where(l => l.TextOrientation == TextOrientation.Other).ToList();
        drawn.Should().NotBeEmpty();
        drawn.Should().OnlyContain(l => l.Location.X > 0 && l.Location.X < page.Width && l.Location.Y > 0 && l.Location.Y < page.Height);
        drawn.Average(l => l.Location.X).Should().BeApproximately(page.Width / 2, 60);
        drawn.Average(l => l.Location.Y).Should().BeApproximately(page.Height / 2, 60);
    }

    [Fact]
    public void Render_WithoutWatermark_HasNoneOfIt()
    {
        // Act
        var pages = PageTexts(DocumentPdf.Render(LongModel()));

        // Assert
        pages.Should().NotContain(p => p.Contains("Compartilhado"));
    }

    [Fact]
    public void Render_WithAVeryLongRecipientName_StillRenders()
    {
        // Act
        var pdf = DocumentPdf.Render(LongModel(), new DocumentWatermark(new string('M', 200), Opened));

        // Assert
        pdf.Take(4).Should().Equal("%PDF"u8.ToArray());
    }
}
