using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Documents;

public sealed record DocumentPdfSection(string? Heading, string Text);

// What the exported PDF shows (design/specs/2026-10-19-psychological-documents-design.md, section 3).
// The identification block depends on the kind (spec 2026-10-20, section 6).
public sealed record DocumentPdfLine(string Label, string Value);

public sealed record DocumentPdfModel(
    string Title, string ProviderName, string Crp, string PatientName,
    string? IdentificationHeading, IReadOnlyList<DocumentPdfLine> Identification,
    IReadOnlyList<DocumentPdfSection> Sections, string PracticeCity, DateTimeOffset SignedAt, string Code)
{
    private const string ParagraphBreak = "\n\n";

    public string FileName => FileNameFor(Code);

    public static string CodeFor(Guid documentId) => documentId.ToString("N")[..8].ToUpperInvariant();

    public static string FileNameFor(Guid documentId) => FileNameFor(CodeFor(documentId));

    private static string FileNameFor(string code) => $"documento-{code}.pdf";

    public static DocumentPdfModel From(ClinicalDocument document, ConformityRuleset ruleset, Patient patient, Provider provider)
    {
        var name = document.SignedByName ?? provider.Name;
        var crp = document.SignedByCrp ?? "";
        var patientName = document.SignedPatientName ?? $"{patient.FirstName} {patient.LastName}".Trim();
        var (heading, lines) = DocumentSections.Identification(
            document.Kind, document.Fields, document.Team, name, crp, patientName);
        return new(
            DocumentCatalog.KindTitle(document.Kind),
            name,
            crp,
            patientName,
            heading,
            [.. lines.Where(l => l.Value.Trim().Length > 0)],
            SectionsOf(document, ruleset, name),
            document.SignedCity ?? provider.PracticeCity?.Trim() ?? "",
            document.SignedAt!.Value,
            CodeFor(document.Id));
    }

    private static IReadOnlyList<DocumentPdfSection> SectionsOf(ClinicalDocument document, ConformityRuleset ruleset, string name) =>
        document.Kind switch
        {
            "relatorio" => DocumentBody.ProviderFieldOrder(document.Kind, ruleset.Elements)
                .Select(code => (code, text: document.Fields.TryGetValue(code, out var value) ? value.Trim() : ""))
                .Where(x => x.text.Length > 0)
                .Select(x => new DocumentPdfSection(DocumentCatalog.Element(document.Kind, x.code).Label, x.text))
                .ToList(),
            "laudo" or "parecer" or "relatorio_multiprofissional" =>
                DocumentSections.Build(document.Kind, ruleset.Elements, document.Fields, document.Team, name),
            _ => document.Body.Split(ParagraphBreak, StringSplitOptions.RemoveEmptyEntries)
                .Select(paragraph => new DocumentPdfSection(null, paragraph))
                .ToList(),
        };
}
