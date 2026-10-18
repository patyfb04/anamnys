using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace Anamnys.Server.Notes;

public static class NotePdf
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static byte[] Render(NotePdfModel m) =>
        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(11));

            page.Header().Column(col =>
            {
                col.Item().Text(m.ProviderName).FontSize(14).SemiBold();
                col.Item().Text($"CRP {m.Crp}");
            });

            page.Content().PaddingVertical(12).Column(col =>
            {
                col.Spacing(8);
                col.Item().Text(m.PatientName).FontSize(13).SemiBold();
                if (m.PatientBirthDate is { } birth)
                {
                    col.Item().Text($"Nascimento: {birth.ToString("dd/MM/yyyy", PtBr)}");
                }
                col.Item().Text($"Atendimento: {Local(m.SessionDate).ToString("dd/MM/yyyy", PtBr)}");
                col.Item().Text($"Formato: {m.FormatName}");
                foreach (var section in m.Sections)
                {
                    col.Item().PaddingTop(6).Text(section.Title).SemiBold();
                    col.Item().Text(string.IsNullOrWhiteSpace(section.Content) ? "Nada registrado" : section.Content);
                }
                col.Item().PaddingTop(16).Text(
                    $"Assinado eletronicamente no Anamnys por {m.ProviderName}, CRP {m.Crp}, em {Local(m.SignedAt).ToString("dd/MM/yyyy HH:mm", PtBr)}.");
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"Nota {m.Code}").FontSize(9);
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
