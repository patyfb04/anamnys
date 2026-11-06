using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Anamnys.Server.Dossiers;

// One line of the chronological summary: Kind is nota, documento, documento externo or divulgação; File is the path in the
// ZIP, or why the file is not included.
public sealed record DossierSummaryRow(DateTimeOffset At, string Kind, string Title, string File);

public sealed record DossierAccessRow(DateTimeOffset At, string Outcome, string? UserAgent);

public sealed record DossierLinkRow(
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? BlockedAt, int ViewsUsed, int? ViewCap,
    bool DownloadEnabled, IReadOnlyList<DossierAccessRow> Accesses);

public sealed record DossierDisclosureRow(
    string Document, string Recipient, string Role, string Purpose, DateTimeOffset AuthorizedAt, string ObtainedVia, string TermFile,
    IReadOnlyList<DossierLinkRow> Links);

public sealed record DossierComplianceRow(string Document, DateTimeOffset SignedAt, string Status, IReadOnlyList<string> Pending);

public sealed record DossierRetentionRow(DateOnly? LastRecordAt, DateOnly? RetentionUntil, DateTimeOffset? ArchivedAt, bool HasDisposalTerm);

public sealed record DossierIntegrityRow(
    string Subject, DateTimeOffset SignedAt, string Status, long? Sequence, DateTimeOffset? SealedAt, bool Retroactive, bool Anchored);

public sealed record DossierAnchorRow(long Sequence, DateTimeOffset AnchoredAt, string TsaUrl, string File);

// What dossie.pdf shows (design/specs/2026-11-04-patient-dossier-design.md §5). Built from DossierData only: no draft,
// transcript, audit value or other patient reaches it.
public sealed record DossierPdfModel(
    int Number, string ProviderName, string Crp, string PatientName, DateOnly? PatientBirthDate, DateTimeOffset GeneratedAt,
    DateOnly? PeriodFrom, DateOnly? PeriodTo, string Reason, bool IntegrityVerified, IReadOnlyList<string> IntegrityProblems,
    IReadOnlyList<DossierSummaryRow> Summary, IReadOnlyList<DossierDisclosureRow> Disclosures,
    IReadOnlyList<DossierComplianceRow> Compliance, DossierRetentionRow Retention, IReadOnlyList<DossierIntegrityRow> Integrity,
    IReadOnlyList<DossierAnchorRow> Anchors);

public static class DossierPdf
{
    public const string Statement =
        "Dossiê gerado pelo Anamnys a partir do prontuário assinado. Os arquivos anexos estão listados com seus hashes SHA-256 em manifesto.json.";

    public const string ReceiptStatement =
        "O hash SHA-256 do arquivo ZIP deste dossiê e o seu carimbo de tempo não podem constar dentro do próprio ZIP: estão no "
        + "comprovante de entrega do dossiê, disponível para o profissional no Anamnys.";

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static string ReasonText(string reason, string? note) => reason switch
    {
        "crp" => "Pedido do CRP",
        "court" => "Intimação ou ordem judicial",
        "patient_request" => "Solicitação do próprio paciente",
        _ => string.IsNullOrWhiteSpace(note) ? "Outro" : $"Outro: {note.Trim()}",
    };

    public static string PeriodText(DateOnly? from, DateOnly? to) => (from, to) switch
    {
        (null, null) => "histórico completo",
        ({ } f, null) => $"a partir de {Day(f)}",
        (null, { } t) => $"até {Day(t)}",
        ({ } f, { } t) => $"de {Day(f)} a {Day(t)}",
    };

    public static byte[] Render(DossierPdfModel m) =>
        Document.Create(container => container.Page(page =>
        {
            var crp = m.Crp.Length > 0 ? $"CRP {m.Crp}" : "CRP não informado";
            page.Size(PageSizes.A4);
            page.Margin(2, Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(10));

            page.Content().Column(col =>
            {
                col.Spacing(6);
                Cover(col, m, crp);
                col.Item().PageBreak();
                Summary(col, m);
                Disclosures(col, m);
                Compliance(col, m);
                Retention(col, m);
                Integrity(col, m);
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"{m.ProviderName} · {crp} · Dossiê nº {m.Number:000}").FontSize(8);
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

    private static void Cover(ColumnDescriptor col, DossierPdfModel m, string crp)
    {
        col.Item().AlignCenter().PaddingBottom(4).Text("DOSSIÊ DO PACIENTE").FontSize(18).Bold();
        col.Item().AlignCenter().PaddingBottom(12).Text($"Nº {m.Number:000}").FontSize(12);
        Line(col, "Profissional", $"{m.ProviderName}, {crp}");
        Line(col, "Paciente", m.PatientName);
        Line(col, "Data de nascimento", m.PatientBirthDate is { } birth ? Day(birth) : "não informada");
        Line(col, "Gerado em", Time(m.GeneratedAt));
        Line(col, "Período solicitado", PeriodText(m.PeriodFrom, m.PeriodTo));
        Line(col, "Motivo", m.Reason);
        Line(col, "Integridade", m.IntegrityVerified
            ? "todos os registros assinados foram verificados na geração e estão íntegros"
            : "foram encontrados problemas na verificação (veja abaixo e a seção Integridade)");
        foreach (var problem in m.IntegrityProblems)
        {
            col.Item().PaddingLeft(12).Text($"• {problem}");
        }
        col.Item().PaddingTop(12).Text(Statement).Justify();
        col.Item().Text(ReceiptStatement).Justify();
    }

    private static void Summary(ColumnDescriptor col, DossierPdfModel m)
    {
        Heading(col, "1. Resumo cronológico");
        if (m.Summary.Count == 0)
        {
            col.Item().Text("Nenhum registro no período.");
            return;
        }
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(62);
                c.ConstantColumn(72);
                c.RelativeColumn(3);
                c.RelativeColumn(4);
            });
            table.Header(h =>
            {
                foreach (var title in new[] { "Data", "Tipo", "Título", "Arquivo no ZIP" })
                {
                    h.Cell().BorderBottom(1).PaddingBottom(2).Text(title).SemiBold();
                }
            });
            foreach (var r in m.Summary)
            {
                Cell(table, DayOf(r.At));
                Cell(table, r.Kind);
                Cell(table, r.Title);
                Cell(table, r.File);
            }
        });
    }

    private static void Disclosures(ColumnDescriptor col, DossierPdfModel m)
    {
        Heading(col, "2. Divulgações");
        if (m.Disclosures.Count == 0)
        {
            col.Item().Text("Nenhuma divulgação registrada no período.");
            return;
        }
        foreach (var d in m.Disclosures)
        {
            col.Item().PaddingTop(4).Text($"{d.Document} → {d.Recipient} ({d.Role})").SemiBold();
            Line(col, "Finalidade", d.Purpose);
            Line(col, "Autorizado em", Time(d.AuthorizedAt));
            Line(col, "Como foi obtida", d.ObtainedVia);
            Line(col, "Termo", d.TermFile);
            if (d.Links.Count == 0)
            {
                col.Item().PaddingLeft(12).Text("Nenhum link criado.");
            }
            foreach (var l in d.Links)
            {
                var cap = l.ViewCap is { } c ? $"{l.ViewsUsed.ToString(PtBr)} de {c.ToString(PtBr)}" : l.ViewsUsed.ToString(PtBr);
                var revoked = l.RevokedAt is { } r ? $" · revogado em {Time(r)}" : "";
                var blocked = l.BlockedAt is { } b ? $" · bloqueado em {Time(b)}" : "";
                col.Item().PaddingLeft(12).Text(
                    $"Link criado em {Time(l.CreatedAt)} · expira em {Time(l.ExpiresAt)}{revoked}{blocked} · visualizações: {cap} · "
                    + $"download {(l.DownloadEnabled ? "permitido" : "não permitido")}");
                if (l.Accesses.Count == 0)
                {
                    col.Item().PaddingLeft(24).Text("Nenhum acesso.");
                }
                foreach (var a in l.Accesses)
                {
                    col.Item().PaddingLeft(24).Text($"{Time(a.At)} · {a.Outcome} · {a.UserAgent ?? "navegador não informado"}").FontSize(9);
                }
            }
        }
    }

    private static void Compliance(ColumnDescriptor col, DossierPdfModel m)
    {
        Heading(col, "3. Conformidade dos documentos emitidos");
        if (m.Compliance.Count == 0)
        {
            col.Item().Text("Nenhum documento emitido no período.");
            return;
        }
        foreach (var c in m.Compliance)
        {
            col.Item().Text(t =>
            {
                t.Span($"{c.Document} (assinado em {DayOf(c.SignedAt)}): ").SemiBold();
                t.Span(c.Status);
            });
            foreach (var pending in c.Pending)
            {
                col.Item().PaddingLeft(12).Text($"• {pending}");
            }
        }
    }

    private static void Retention(ColumnDescriptor col, DossierPdfModel m)
    {
        Heading(col, "4. Guarda do prontuário");
        var r = m.Retention;
        Line(col, "Último registro", r.LastRecordAt is { } last ? Day(last) : "nenhum registro ainda");
        Line(col, "Guardar até", r.RetentionUntil is { } until ? Day(until) : "sem prazo calculado (nenhum registro ainda)");
        Line(col, "Situação", r.ArchivedAt is { } archived ? $"arquivado em {DayOf(archived)}" : "ativo");
        Line(col, "Termo de descarte", r.HasDisposalTerm ? "existe" : "não existe");
    }

    private static void Integrity(ColumnDescriptor col, DossierPdfModel m)
    {
        Heading(col, "5. Integridade");
        if (m.Integrity.Count == 0)
        {
            col.Item().Text("Nenhuma nota ou documento assinado no período.");
        }
        foreach (var i in m.Integrity)
        {
            var sequence = i.Sequence is { } n ? $"selo nº {n.ToString(PtBr)}" : "sem selo";
            var sealedAt = i.SealedAt is { } at ? $" · selado em {Time(at)}" : "";
            col.Item().Text(
                $"{i.Subject} (assinado em {DayOf(i.SignedAt)}) · {i.Status} · {sequence}{sealedAt} · "
                + $"{(i.Retroactive ? "selo retroativo" : "selado na assinatura")} · {(i.Anchored ? "carimbado" : "ainda sem carimbo")}");
        }
        col.Item().PaddingTop(4).Text("Carimbos de tempo usados").SemiBold();
        if (m.Anchors.Count == 0)
        {
            col.Item().Text("Nenhum carimbo de tempo cobre estes registros ainda.");
        }
        foreach (var a in m.Anchors)
        {
            col.Item().Text($"Selo nº {a.Sequence.ToString(PtBr)} · {Time(a.AnchoredAt)} · {a.TsaUrl} · {a.File}");
        }
        col.Item().PaddingTop(4).Text("Como verificar").SemiBold();
        col.Item().Text(
            "Cada nota e documento assinado recebe, na assinatura, um selo com o hash SHA-256 do seu conteúdo, encadeado ao "
            + "selo anterior; o hash do último selo é carimbado diariamente por uma Autoridade de Carimbo de Tempo (RFC 3161). "
            + "O comprovante em integridade/comprovante.pdf traz o resultado de cada selo e o hash de cada selo carimbado; os "
            + "tokens dos carimbos estão em integridade/carimbos/. LEIA-ME.txt explica como conferir os arquivos e os tokens "
            + "com ferramentas padrão (sha256sum, openssl).")
            .Justify();
    }

    private static void Heading(ColumnDescriptor col, string text) =>
        col.Item().PaddingTop(10).PaddingBottom(2).Text(text).FontSize(13).SemiBold();

    private static void Cell(TableDescriptor table, string text) =>
        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(2).PaddingRight(4).Text(text).FontSize(9);

    private static void Line(ColumnDescriptor col, string label, string value) =>
        col.Item().Text(t =>
        {
            t.Span($"{label}: ").SemiBold();
            t.Span(value);
        });

    private static string Day(DateOnly date) => date.ToString("dd/MM/yyyy", PtBr);

    private static string DayOf(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Practice).ToString("dd/MM/yyyy", PtBr);

    private static string Time(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Practice).ToString("dd/MM/yyyy HH:mm", PtBr);
}
