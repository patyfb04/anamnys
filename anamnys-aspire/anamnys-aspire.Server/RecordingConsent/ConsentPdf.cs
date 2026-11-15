using QuestPDF.Fluent;
using QuestPDF.Helpers;

namespace Anamnys.Server.RecordingConsents;

public sealed record ConsentPdfModel(
    Guid ConsentId, string Version, string TermText, string TermSha256, string SignerName, string SignerRole, string? Relationship,
    DateTimeOffset SignedAt, string ProviderName, string Crp);

// The signed term (design/specs/2026-11-15-recording-consent-design.md §3): the text the signer accepted, the signer,
// role, relationship, date-time and the text's SHA-256. Generated at signing and kept in the private bucket.
public static class ConsentPdf
{
    public static string ObjectKeyFor(Guid providerId, Guid consentId) => $"recording-consents/{providerId}/{consentId}.pdf";

    public static string CodeFor(Guid consentId) => consentId.ToString("N")[..8].ToUpperInvariant();

    public static string FileNameFor(Guid consentId) => $"termo-consentimento-gravacao-{CodeFor(consentId)}.pdf";

    public static byte[] Render(ConsentPdfModel m) =>
        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
            page.DefaultTextStyle(t => t.FontSize(10.5f));

            page.Content().PaddingVertical(8).Column(col =>
            {
                col.Spacing(4);
                var lines = m.TermText.Split('\n');
                var first = true;
                foreach (var line in lines)
                {
                    if (first)
                    {
                        col.Item().AlignCenter().PaddingBottom(8).Text(line).FontSize(14).Bold();
                        first = false;
                        continue;
                    }
                    if (line.Length == 0)
                    {
                        col.Item().Height(4);
                    }
                    else if (IsHeading(line))
                    {
                        col.Item().PaddingTop(2).Text(line).SemiBold();
                    }
                    else
                    {
                        col.Item().Text(line).Justify();
                    }
                }

                col.Item().PaddingTop(14).LineHorizontal(0.5f);
                col.Item().PaddingTop(6).Text("Registro da assinatura eletrônica").SemiBold();
                Line(col, "Assinado por", m.SignerName);
                Line(col, "Na qualidade de", m.SignerRole == ConsentRules.RoleGuardian ? "Responsável legal" : "Paciente");
                if (m.SignerRole == ConsentRules.RoleGuardian && !string.IsNullOrWhiteSpace(m.Relationship))
                {
                    Line(col, "Grau de parentesco", m.Relationship);
                }
                Line(col, "Data e hora", $"{ConsentTerm.LocalDateTime(m.SignedAt)} (horário de Brasília)");
                Line(col, "Identificação", "data de nascimento do(a) paciente conferida com o prontuário");
                Line(col, "Versão do termo", m.Version);
                Line(col, "SHA-256 do texto", m.TermSha256);
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"{m.ProviderName} · {m.Crp} · Termo {CodeFor(m.ConsentId)}").FontSize(8.5f);
                row.RelativeItem().AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8.5f));
                    t.Span("Página ");
                    t.CurrentPageNumber();
                    t.Span(" de ");
                    t.TotalPages();
                });
            });
        })).GeneratePdf();

    private static bool IsHeading(string line) =>
        line == "Observações do profissional" || (line.Length < 40 && char.IsAsciiDigit(line[0]) && line.Contains(". "));

    private static void Line(ColumnDescriptor col, string label, string value) =>
        col.Item().Text(t =>
        {
            t.Span($"{label}: ").SemiBold();
            t.Span(value);
        });
}
