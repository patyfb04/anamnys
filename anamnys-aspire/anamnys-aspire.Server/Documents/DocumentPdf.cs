using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace Anamnys.Server.Documents;

public static class DocumentPdf
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private const float WatermarkAngle = -45;
    private const float WatermarkWidth = 560;
    private const float WatermarkHeight = 240;
    private const string WatermarkColor = "#4D808080";

    public static byte[] Render(DocumentPdfModel m) => Render(m, null);

    public static byte[] Render(DocumentPdfModel m, DocumentWatermark? watermark) =>
        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(11));

            if (watermark is not null)
            {
                // The page's foreground repeats on every page. Moved to the page's centre, rotated there, and the text
                // block centred on that point; Unconstrained lets the rotated block pass the page's width.
                page.Foreground().TranslateX(PageSizes.A4.Width / 2).TranslateY(PageSizes.A4.Height / 2).Rotate(WatermarkAngle)
                    .TranslateX(-WatermarkWidth / 2).TranslateY(-WatermarkHeight / 2).Unconstrained().Width(WatermarkWidth).Height(WatermarkHeight)
                    .AlignMiddle().Text(t =>
                    {
                        t.AlignCenter();
                        t.DefaultTextStyle(s => s.FontSize(20).FontColor(WatermarkColor));
                        t.Line($"Compartilhado com {watermark.RecipientName}");
                        t.Span(Local(watermark.OpenedAt).ToString("dd/MM/yyyy HH:mm", PtBr));
                    });
            }

            page.Content().PaddingVertical(12).Column(col =>
            {
                col.Spacing(10);
                col.Item().AlignCenter().PaddingBottom(8).Text(m.Title.ToUpper(PtBr)).FontSize(16).Bold();
                if (m.IdentificationHeading is not null)
                {
                    col.Item().PaddingTop(6).Text(m.IdentificationHeading).SemiBold();
                }
                foreach (var line in m.Identification)
                {
                    col.Item().Text(t =>
                    {
                        t.Span($"{line.Label}: ").SemiBold();
                        t.Span(line.Value);
                    });
                }
                foreach (var section in m.Sections)
                {
                    if (section.Heading is not null)
                    {
                        col.Item().PaddingTop(6).Text(section.Heading).SemiBold();
                    }
                    col.Item().Text(section.Text).Justify();
                }
                var signedLocal = Local(m.SignedAt);
                var date = signedLocal.ToString("dd 'de' MMMM 'de' yyyy", PtBr);
                col.Item().PaddingTop(16).Text(m.PracticeCity.Length > 0 ? $"{m.PracticeCity}, {date}" : date);
                col.Item().PaddingTop(16).Text(
                    $"Assinado eletronicamente no Anamnys por {m.ProviderName}, CRP {m.Crp}, em {signedLocal.ToString("dd/MM/yyyy HH:mm", PtBr)}.");
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"{m.ProviderName} · CRP {m.Crp}").FontSize(9);
                row.RelativeItem().AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(9));
                    t.Span("Página ");
                    t.CurrentPageNumber();
                    t.Span(" de ");
                    t.TotalPages();
                });
            });
        })).GeneratePdf();

    private static DateTime Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Practice).DateTime;
}
