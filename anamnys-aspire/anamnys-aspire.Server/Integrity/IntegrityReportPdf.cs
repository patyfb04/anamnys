using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace Anamnys.Server.Integrity;

public sealed record IntegrityReportAnchor(long Sequence, string HeadHash, string TsaUrl, DateTimeOffset AnchoredAt, bool Valid);

public sealed record IntegrityReportModel(
    string ProviderName, string Crp, DateTimeOffset GeneratedAt, ChainIntegrity Chain, long? HeadSequence, string? HeadHash,
    IReadOnlyList<IntegrityReportAnchor> Anchors);

// One seal of a restricted report: Subject is "Nota <código>" or "Documento <código>"; Sequence and SealedAt are null for a
// signed record without a seal. Status is a SubjectStatus value.
public sealed record IntegrityReportSeal(string Subject, long? Sequence, string Status, DateTimeOffset? SealedAt, bool Retroactive, bool Anchored);

// The report restricted to a set of seals (the patient dossier, design/specs/2026-11-04-patient-dossier-design.md §4):
// each seal with its verification at GeneratedAt, and the anchors that cover them, whose tokens travel with the report.
public sealed record IntegritySubjectsReportModel(
    string ProviderName, string Crp, DateTimeOffset GeneratedAt, IReadOnlyList<IntegrityReportSeal> Seals,
    IReadOnlyList<IntegrityReportAnchor> Anchors, string TokenFolder);

// The verification report (design/specs/2026-11-03-integrity-chain-design.md §8): ids, hashes, dates and results only,
// never clinical text. The tokens themselves are not embedded in this delivery.
public static class IntegrityReportPdf
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    // The restricted report: only the given seals and their anchors, never the rest of the provider's chain.
    public static byte[] Render(IntegritySubjectsReportModel m) =>
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
                Line(col, "Verificado em", Time(m.GeneratedAt));
                var problems = m.Seals.Count(s => s.Status != SubjectStatus.Intact);
                Line(col, "Resultado", problems == 0
                    ? "íntegro: nenhuma alteração detectada"
                    : $"problema: {problems.ToString(PtBr)} de {m.Seals.Count.ToString(PtBr)} registros não conferem");
                Line(col, "Registros verificados", m.Seals.Count.ToString(PtBr));

                col.Item().PaddingTop(6).Text("Selos").SemiBold();
                if (m.Seals.Count == 0)
                {
                    col.Item().Text("Nenhum registro assinado.");
                }
                foreach (var s in m.Seals)
                {
                    var sequence = s.Sequence is { } n ? $"selo nº {n.ToString(PtBr)}" : "sem selo";
                    var sealedAt = s.SealedAt is { } at ? $" · selado em {Time(at)}{(s.Retroactive ? " (retroativo)" : "")}" : "";
                    col.Item().Text(
                        $"{s.Subject} · {sequence}{sealedAt} · {StatusText(s.Status)} · {(s.Anchored ? "carimbado" : "ainda sem carimbo")}");
                }

                col.Item().PaddingTop(6).Text("Carimbos de tempo (RFC 3161)").SemiBold();
                if (m.Anchors.Count == 0)
                {
                    col.Item().Text("Nenhum carimbo de tempo cobre estes selos ainda.");
                }
                foreach (var a in m.Anchors)
                {
                    col.Item().Column(anchor =>
                    {
                        anchor.Item().Text(
                            $"Selo nº {a.Sequence.ToString(PtBr)} · {Time(a.AnchoredAt)} · {a.TsaUrl} · {(a.Valid ? "válido" : "inválido")} · "
                            + $"token {m.TokenFolder}{a.Sequence.ToString(CultureInfo.InvariantCulture)}.tsr");
                        anchor.Item().Text(a.HeadHash).FontSize(8);
                    });
                }

                col.Item().PaddingTop(6).Text("Como verificar").SemiBold();
                col.Item().Text(HowItWorks).Justify();
                col.Item().Text(
                    "Um carimbo cobre todos os selos de número igual ou menor que o seu. Para conferi-lo, grave o hash do selo "
                    + "carimbado, exatamente como acima e sem quebra de linha, num arquivo, e verifique o token com o certificado "
                    + "raiz da autoridade:")
                    .Justify();
                col.Item().Text($"printf '%s' <hash> > cabeca.txt\nopenssl ts -verify -data cabeca.txt -in {m.TokenFolder}<nº>.tsr -token_in -CAfile raiz.pem")
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

    public static string StatusText(string status) => status switch
    {
        SubjectStatus.Intact => "íntegro",
        SubjectStatus.ContentChanged => "conteúdo difere do selado",
        SubjectStatus.ChainBroken => "cadeia rompida neste selo",
        SubjectStatus.Discarded => "registro descartado",
        _ => "sem selo",
    };

    private const string HowItWorks =
        "Cada nota e cada documento assinados recebem, no momento da assinatura, um selo com o hash SHA-256 do seu "
        + "conteúdo. Cada selo inclui o hash do selo anterior, formando uma cadeia: qualquer alteração, remoção ou "
        + "reordenação de um registro depois da assinatura muda os hashes seguintes e é detectada. Uma vez por dia, o "
        + "hash do último selo é carimbado por uma Autoridade de Carimbo de Tempo (RFC 3161); apenas esse hash sai do "
        + "sistema.";

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
                col.Item().Text(HowItWorks).Justify();
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
