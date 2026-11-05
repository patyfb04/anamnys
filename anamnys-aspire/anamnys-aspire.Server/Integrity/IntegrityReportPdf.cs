using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace Anamnys.Server.Integrity;

public sealed record IntegrityReportAnchor(long Sequence, string HeadHash, string TsaUrl, DateTimeOffset AnchoredAt, bool Valid);

public sealed record IntegrityReportModel(
    string ProviderName, string Crp, DateTimeOffset GeneratedAt, ChainIntegrity Chain, long? HeadSequence, string? HeadHash,
    IReadOnlyList<IntegrityReportAnchor> Anchors);

// The verification report (design/specs/2026-11-03-integrity-chain-design.md §8): ids, hashes, dates and results only,
// never clinical text. The tokens themselves are not embedded in this delivery.
public static class IntegrityReportPdf
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static byte[] Render(IntegrityReportModel m) =>
        Document.Create(container => container.Page(page =>
        {
            var crp = m.Crp.Length > 0 ? $"CRP {m.Crp}" : "CRP não informado";
            page.Size(PageSizes.A4);
            page.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(11));

            page.Content().PaddingVertical(12).Column(col =>
            {
                col.Spacing(8);
                col.Item().AlignCenter().PaddingBottom(8).Text("COMPROVANTE DE INTEGRIDADE DOS REGISTROS").FontSize(16).Bold();
                Line(col, "Profissional", $"{m.ProviderName}, {crp}");
                Line(col, "Gerado em", Time(m.GeneratedAt));
                Line(col, "Resultado", Result(m.Chain.FirstProblem));
                Line(col, "Registros selados", m.Chain.TotalSeals.ToString(PtBr));
                Line(col, "Selos de registros descartados", m.Chain.Discarded.ToString(PtBr));
                if (m.HeadSequence is { } sequence && m.HeadHash is { } hash)
                {
                    Line(col, "Último selo (cabeça da cadeia)", $"nº {sequence.ToString(PtBr)}");
                    col.Item().Text(hash).FontSize(8);
                }

                col.Item().PaddingTop(6).Text("Carimbos de tempo (RFC 3161)").SemiBold();
                if (m.Anchors.Count == 0)
                {
                    col.Item().Text("Nenhum carimbo de tempo registrado ainda.");
                }
                foreach (var a in m.Anchors)
                {
                    col.Item().Column(anchor =>
                    {
                        anchor.Item().Text(
                            $"Selo nº {a.Sequence.ToString(PtBr)} · {Time(a.AnchoredAt)} · {a.TsaUrl} · {(a.Valid ? "válido" : "inválido")}");
                        anchor.Item().Text(a.HeadHash).FontSize(8);
                    });
                }

                col.Item().PaddingTop(6).Text("Como verificar").SemiBold();
                col.Item().Text(
                    "Cada nota e cada documento assinados recebem, no momento da assinatura, um selo com o hash SHA-256 do seu "
                    + "conteúdo. Cada selo inclui o hash do selo anterior, formando uma cadeia: qualquer alteração, remoção ou "
                    + "reordenação de um registro depois da assinatura muda os hashes seguintes e é detectada. Uma vez por dia, o "
                    + "hash do último selo é carimbado por uma Autoridade de Carimbo de Tempo (RFC 3161); apenas esse hash sai do "
                    + "sistema.")
                    .Justify();
                col.Item().Text(
                    "Qualquer pessoa pode conferir um carimbo com ferramentas padrão. Grave o hash do selo, exatamente como acima e "
                    + "sem quebra de linha, num arquivo, e verifique o token do carimbo (fornecido mediante solicitação) com o "
                    + "certificado raiz da autoridade:")
                    .Justify();
                col.Item().Text("printf '%s' <hash> > cabeca.txt\nopenssl ts -verify -data cabeca.txt -in token.tsr -token_in -CAfile raiz.pem")
                    .FontSize(9);
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"{m.ProviderName} · {crp} · Comprovante de integridade").FontSize(9);
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

    private static string Result(ChainProblem? problem) => problem switch
    {
        null => "íntegro: nenhuma alteração detectada",
        { Kind: ChainProblemKind.SequenceGap } => $"problema: o selo nº {problem.Sequence.ToString(PtBr)} está ausente",
        { Kind: ChainProblemKind.ContentChanged } => $"problema: o conteúdo do registro do selo nº {problem.Sequence.ToString(PtBr)} difere do selado",
        { Kind: ChainProblemKind.AnchorInvalid } => $"problema: o carimbo de tempo do selo nº {problem.Sequence.ToString(PtBr)} não confere",
        _ => $"problema: a cadeia está rompida no selo nº {problem.Sequence.ToString(PtBr)}",
    };

    private static string Time(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, Practice).ToString("dd/MM/yyyy HH:mm", PtBr);

    private static void Line(ColumnDescriptor col, string label, string value) =>
        col.Item().Text(t =>
        {
            t.Span($"{label}: ").SemiBold();
            t.Span(value);
        });
}
