using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Notes;

public sealed record NotePdfSection(string Title, string Content);

// What the exported PDF shows (design/specs/2026-10-17-note-pdf-export-design.md §5.2).
// Built from the signed note only: the transcript / typed original text never reaches it.
public sealed record NotePdfModel(
    string ProviderName, string Crp, string PatientName, DateOnly? PatientBirthDate,
    DateTimeOffset SessionDate, string FormatName, IReadOnlyList<NotePdfSection> Sections,
    DateTimeOffset SignedAt, string Code)
{
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["data"] = "Dados", ["assessment"] = "Avaliação", ["plan"] = "Plano",
        ["subjective"] = "Subjetivo", ["objective"] = "Objetivo",
        ["biological"] = "Biológico", ["psychological"] = "Psicológico", ["social"] = "Social",
        // The default intake template's sections (F-31); an own template's other sections show their key.
        ["identificacao"] = "Identificação", ["queixa"] = "Queixa principal", ["historia"] = "História pessoal e familiar",
        ["saude"] = "Saúde", ["contexto"] = "Contexto atual", ["objetivos"] = "Objetivos",
    };

    public string FileName => FileNameFor(Code);

    public static string SectionTitle(string key) => Titles.GetValueOrDefault(key, key);

    public static string CodeFor(Guid noteId) => noteId.ToString("N")[..8].ToUpperInvariant();

    public static string FileNameFor(Guid noteId) => FileNameFor(CodeFor(noteId));

    private static string FileNameFor(string code) => $"nota-{code}.pdf";

    // titles: an intake note's own section titles (F-31); other keys use the fixed titles.
    public static NotePdfModel From(
        Note note, IReadOnlyList<NoteSection> sections, Patient patient, Provider provider, Session? session,
        IReadOnlyDictionary<string, string>? titles = null) =>
        new(
            provider.Name,
            note.SignedByCrp ?? "",
            $"{patient.FirstName} {patient.LastName}".Trim(),
            patient.DateOfBirth,
            session?.ScheduledStart ?? session?.ActualStart ?? note.CreatedAt,
            note.Format == "Biopsychosocial" ? "Biopsicossocial" : note.Format,
            sections.OrderBy(s => s.Position)
                .Select(s => new NotePdfSection(titles?.GetValueOrDefault(s.Key) ?? SectionTitle(s.Key), s.Content.Trim()))
                .ToList(),
            note.SignedAt!.Value,
            CodeFor(note.Id));
}
