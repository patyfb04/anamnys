using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace Anamnys.Server.Documents;

public static class DocumentPdf
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static byte[] Render(DocumentPdfModel m) =>
        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(11));

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
