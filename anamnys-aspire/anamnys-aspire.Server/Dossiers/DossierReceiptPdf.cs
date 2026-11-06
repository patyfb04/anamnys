using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Anamnys.Server.Dossiers;

// What the delivery receipt shows (design/specs/2026-11-04-patient-dossier-design.md §5): the ZIP's SHA-256 and its
// timestamp, which cannot be inside the ZIP itself. Authority is null when the token names no signer; AnchoredAt is null
// when the dossier went out without a timestamp.
public sealed record DossierReceiptModel(
    int Number, string ProviderName, string Crp, string PatientName, DateTimeOffset RequestedAt, DateTimeOffset GeneratedAt,
    string Reason, string Period, string ZipSha256, DateTimeOffset? AnchoredAt, string? Authority,
    IReadOnlyList<DossierManifestFile> Files, string Status, DateTimeOffset IssuedAt, string ZipFileName, string TokenFileName);

public static class DossierReceiptPdf
{
    public const string NoTimestamp =
        "sem carimbo: a Autoridade de Carimbo de Tempo não respondeu quando o dossiê foi gerado, e o dossiê foi entregue sem carimbo de tempo.";

    public const string HowToCheck =
        "Para conferir: calcule o SHA-256 do arquivo ZIP recebido (por exemplo, sha256sum) e compare com o valor acima. Cada "
        + "arquivo dentro do ZIP é conferido com o manifesto.json, que lista o SHA-256 de todos eles (veja LEIA-ME.txt).";

    // The exact check of the ZIP's timestamp; null when the dossier went out without one.
    public static string? TokenCheck(DossierReceiptModel m) => m.AnchoredAt is null
        ? null
        : $"O carimbo de tempo (RFC 3161) foi emitido sobre o SHA-256 do próprio arquivo ZIP. O token está no arquivo "
          + $"{m.TokenFileName}, baixado no Anamnys junto com este comprovante. Para conferir, com o certificado raiz da "
          + "autoridade de carimbo de tempo:";

    public static string TokenCommand(DossierReceiptModel m) =>
        $"openssl ts -verify -data {m.ZipFileName} -in {m.TokenFileName} -token_in -CAfile <certificado-raiz-da-autoridade.pem>";

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static string TimestampText(DateTimeOffset? anchoredAt, string? authority) => anchoredAt is { } at
        ? $"{Seconds(at)}, emitido por {authority ?? "autoridade não identificada no token"}"
        : NoTimestamp;

    public static byte[] Render(DossierReceiptModel m) =>
        Document.Create(container => container.Page(page =>
        {
            var crp = m.Crp.Length > 0 ? $"CRP {m.Crp}" : "CRP não informado";
            page.Size(PageSizes.A4);
            page.Margin(2, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(10));

            page.Content().Column(col =>
            {
                col.Spacing(5);
                col.Item().AlignCenter().PaddingBottom(4).Text("COMPROVANTE DE ENTREGA DO DOSSIÊ").FontSize(16).Bold();
                col.Item().AlignCenter().PaddingBottom(10).Text($"Dossiê do paciente nº {m.Number:000}").FontSize(11);
                Line(col, "Profissional", $"{m.ProviderName}, {crp}");
                Line(col, "Paciente", m.PatientName);
                Line(col, "Solicitado em", Time(m.RequestedAt));
                Line(col, "Gerado em", Time(m.GeneratedAt));
                Line(col, "Motivo", m.Reason);
                Line(col, "Período solicitado", m.Period);
                Line(col, "Situação", m.Status);

                Heading(col, "Arquivo ZIP entregue");
                Line(col, "SHA-256 do ZIP", m.ZipSha256);
                Line(col, "Carimbo de tempo", TimestampText(m.AnchoredAt, m.Authority));
                Line(col, "Arquivo ZIP", m.ZipFileName);
                col.Item().PaddingTop(4).Text(HowToCheck).FontSize(9).Justify();
                if (TokenCheck(m) is { } tokenCheck)
                {
                    col.Item().PaddingTop(2).Text(tokenCheck).FontSize(9).Justify();
                    col.Item().PaddingLeft(8).Text(TokenCommand(m)).FontSize(8);
                }

                Heading(col, "Arquivos do dossiê (manifesto.json)");
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(4);
                        c.RelativeColumn(1.3f);
                        c.RelativeColumn(6);
                    });
                    Header(table, "Arquivo");
                    Header(table, "Bytes");
                    Header(table, "SHA-256");
                    foreach (var file in m.Files)
                    {
                        Cell(table, file.Path);
                        Cell(table, file.Bytes.ToString("N0", PtBr));
                        Cell(table, file.Sha256);
                    }
                });
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"{m.ProviderName} · {crp} · Comprovante emitido em {Time(m.IssuedAt)}").FontSize(8);
                row.RelativeItem().AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8));
                    t.Span("Página ");
                    t.CurrentPageNumber();
                    t.Span(" de ");
                    t.TotalPages();
                });
            });
        })).GeneratePdf();

    internal static string Day(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Practice).ToString("dd/MM/yyyy", PtBr);

    private static string Time(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Practice).ToString("dd/MM/yyyy HH:mm", PtBr);

    private static string Seconds(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, Practice).ToString("dd/MM/yyyy HH:mm:ss", PtBr) + " (horário de Brasília)";

    private static void Heading(ColumnDescriptor col, string text) =>
        col.Item().PaddingTop(10).PaddingBottom(2).Text(text).FontSize(12).SemiBold();

    private static void Header(TableDescriptor table, string text) =>
        table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(2).Text(text).FontSize(8).SemiBold();

    private static void Cell(TableDescriptor table, string text) =>
        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(2).PaddingRight(4).Text(text).FontSize(7);

    private static void Line(ColumnDescriptor col, string label, string value) =>
        col.Item().Text(t =>
        {
            t.Span($"{label}: ").SemiBold();
            t.Span(value);
        });
}
