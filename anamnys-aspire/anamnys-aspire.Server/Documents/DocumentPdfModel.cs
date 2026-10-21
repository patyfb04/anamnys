using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Documents;

public sealed record DocumentPdfSection(string? Heading, string Text);

// What the exported PDF shows (design/specs/2026-10-19-psychological-documents-design.md, section 3).
// Author and CRP are only printed in the identification block of the relatório.
public sealed record DocumentPdfModel(
    string Title, string ProviderName, string Crp, string PatientName, string? Author,
    IReadOnlyList<DocumentPdfSection> Sections, string PracticeCity, DateTimeOffset SignedAt, string Code)
{
    private const string ParagraphBreak = "\n\n";

    public string FileName => FileNameFor(Code);

    public static string CodeFor(Guid documentId) => documentId.ToString("N")[..8].ToUpperInvariant();

    public static string FileNameFor(Guid documentId) => FileNameFor(CodeFor(documentId));

    private static string FileNameFor(string code) => $"documento-{code}.pdf";

    public static DocumentPdfModel From(ClinicalDocument document, ConformityRuleset ruleset, Patient patient, Provider provider) =>
        new(
            DocumentCatalog.KindTitle(document.Kind),
            document.SignedByName ?? provider.Name,
            document.SignedByCrp ?? "",
            document.SignedPatientName ?? $"{patient.FirstName} {patient.LastName}".Trim(),
            document.Kind == "relatorio" ? document.SignedByName ?? provider.Name : null,
            SectionsOf(document, ruleset),
            document.SignedCity ?? provider.PracticeCity?.Trim() ?? "",
            document.SignedAt!.Value,
            CodeFor(document.Id));

    private static List<DocumentPdfSection> SectionsOf(ClinicalDocument document, ConformityRuleset ruleset) =>
        document.Kind == "relatorio"
            ? DocumentBody.ProviderFieldOrder(document.Kind, ruleset.Elements)
                .Select(code => (code, text: document.Fields.TryGetValue(code, out var value) ? value.Trim() : ""))
                .Where(x => x.text.Length > 0)
                .Select(x => new DocumentPdfSection(DocumentCatalog.Element(document.Kind, x.code).Label, x.text))
                .ToList()
            : document.Body.Split(ParagraphBreak, StringSplitOptions.RemoveEmptyEntries)
                .Select(paragraph => new DocumentPdfSection(null, paragraph))
                .ToList();
}
