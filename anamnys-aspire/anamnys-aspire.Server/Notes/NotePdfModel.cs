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
    };

    public string FileName => FileNameFor(Code);

    public static string SectionTitle(string key) => Titles.GetValueOrDefault(key, key);

    public static string CodeFor(Guid noteId) => noteId.ToString("N")[..8].ToUpperInvariant();

    public static string FileNameFor(Guid noteId) => FileNameFor(CodeFor(noteId));

    private static string FileNameFor(string code) => $"nota-{code}.pdf";

    public static NotePdfModel From(Note note, IReadOnlyList<NoteSection> sections, Patient patient, Provider provider, Session? session) =>
        new(
            provider.Name,
            note.SignedByCrp ?? "",
            $"{patient.FirstName} {patient.LastName}".Trim(),
            patient.DateOfBirth,
            session?.ScheduledStart ?? session?.ActualStart ?? note.CreatedAt,
            note.Format == "Biopsychosocial" ? "Biopsicossocial" : note.Format,
            sections.OrderBy(s => s.Position)
                .Select(s => new NotePdfSection(SectionTitle(s.Key), s.Content.Trim()))
                .ToList(),
            note.SignedAt!.Value,
            CodeFor(note.Id));
}
