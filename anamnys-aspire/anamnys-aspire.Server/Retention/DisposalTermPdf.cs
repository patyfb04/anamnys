using System.Globalization;
using Anamnys.Server.Data.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace Anamnys.Server.Retention;

public sealed record DisposalTermPdfModel(
    Guid TermId, DateTimeOffset CreatedAt, string ProviderName, string Crp, string PatientName, DateOnly PeriodFrom,
    DateOnly PeriodTo, DateOnly RetentionUntil, int GuardYears, string Basis, IReadOnlyList<(string Label, int Count)> Counts)
{
    // What the term lists, in this order; the other deleted rows (sections, excerpts, audit of the deleted notes...)
    // are part of these records and stay in DisposalTerms.Counts.
    public static readonly IReadOnlyList<(string Key, string Label)> Kinds =
    [
        ("notes", "Notas clínicas"),
        ("clinicalDocuments", "Documentos psicológicos"),
        ("externalDocuments", "Documentos externos anexados"),
        ("sessions", "Sessões"),
        ("appointments", "Agendamentos"),
        ("transcripts", "Transcrições"),
        ("scaleApplications", "Aplicações de escalas"),
        ("diagnoses", "Diagnósticos"),
        ("medications", "Medicações"),
        ("treatmentPlans", "Planos terapêuticos"),
        ("planObjectives", "Objetivos terapêuticos"),
    ];

    public string Code => DisposalTerms.CodeFor(TermId);

    // "Encerrado em" is the term's stored RetentionUntil: the patient's deadline as the clock had it at the discard.
    public static DisposalTermPdfModel From(DisposalTerm term, string patientName, Provider provider) =>
        new(
            term.Id,
            term.CreatedAt,
            provider.Name,
            provider.CrpNumber is null ? "" : $"{provider.CrpNumber}/{provider.CrpRegion}",
            patientName.Trim(),
            term.PeriodFrom,
            term.PeriodTo,
            term.RetentionUntil,
            term.GuardYears,
            term.Basis,
            Kinds.Select(k => (k.Label, term.Counts.GetValueOrDefault(k.Key))).ToList());
}

// The disposal term (design/specs/2026-10-28-retention-panel-design.md §3), in the style of the psychological
// documents (Documents/DocumentPdf).
public static class DisposalTermPdf
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static byte[] Render(DisposalTermPdfModel m) =>
        Document.Create(container => container.Page(page =>
        {
            var created = TimeZoneInfo.ConvertTime(m.CreatedAt, Practice).DateTime;
            var crp = m.Crp.Length > 0 ? $"CRP {m.Crp}" : "CRP não informado";
            page.Size(PageSizes.A4);
            page.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(11));

            page.Content().PaddingVertical(12).Column(col =>
            {
                col.Spacing(10);
                col.Item().AlignCenter().PaddingBottom(8).Text("TERMO DE DESCARTE DE PRONTUÁRIO").FontSize(16).Bold();
                Line(col, "Data do descarte", created.ToString("dd/MM/yyyy HH:mm", PtBr));
                Line(col, "Profissional responsável", $"{m.ProviderName}, {crp}");
                Line(col, "Paciente", m.PatientName);
                Line(col, "Período do prontuário", $"{Date(m.PeriodFrom)} a {Date(m.PeriodTo)} (primeiro e último registro)");
                Line(col, "Prazo de guarda", $"{m.GuardYears} anos a partir do último registro, encerrado em {Date(m.RetentionUntil)}");
                Line(col, "Fundamento", m.Basis);

                col.Item().PaddingTop(6).Text("Registros eliminados").SemiBold();
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(3);
                        c.RelativeColumn(1);
                    });
                    foreach (var (label, count) in m.Counts)
                    {
                        table.Cell().PaddingVertical(2).Text(label);
                        table.Cell().PaddingVertical(2).AlignRight().Text(count.ToString(PtBr));
                    }
                });

                col.Item().PaddingTop(6).Text(
                    "Encerrado o prazo de guarda, o conteúdo clínico deste prontuário (notas, documentos, sessões, transcrições, "
                    + "anexos e demais registros acima) foi eliminado de forma definitiva e irreversível do Anamnys, inclusive os "
                    + "arquivos armazenados. Permanecem apenas este termo, o nome do paciente e a trilha de auditoria do descarte.")
                    .Justify();
                col.Item().PaddingTop(16).Text(
                    $"Descarte realizado no Anamnys por {m.ProviderName}, {crp}, em {created.ToString("dd/MM/yyyy 'às' HH:mm", PtBr)}.");
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"{m.ProviderName} · {crp} · Termo {m.Code}").FontSize(9);
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

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", PtBr);

    private static void Line(ColumnDescriptor col, string label, string value) =>
        col.Item().Text(t =>
        {
            t.Span($"{label}: ").SemiBold();
            t.Span(value);
        });
}
