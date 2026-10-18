using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

public sealed record NoteSectionDto(string Key, string Content, int Position, DateTimeOffset GeneratedAt, DateTimeOffset? EditedAt, int Revision);

public sealed record NoteDetail(
    Guid Id, Guid PatientId, string PatientName, string Status, string InputMode, string Format,
    string? RawTranscript, string? FailureReason, DateTimeOffset CreatedAt,
    string? StructureStatus, string? StructureFailure, IReadOnlyList<NoteSectionDto> Sections,
    DateTimeOffset? SignedAt, string? SignedByCrp, bool ProviderHasCrp, bool SectionsByAi,
    DateTimeOffset? ExportedAt);

public static class NoteQueries
{
    public static async Task<NoteDetail?> GetAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct)
    {
        var note = await (from n in db.Notes.AsNoTracking()
                          join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
                          where n.Id == noteId && n.ProviderId == providerId
                          select new
                          {
                              n.Id, n.PatientId, PatientName = p.FirstName + " " + p.LastName, n.Status, n.InputMode, n.Format,
                              n.RawTranscript, n.FailureReason, n.CreatedAt, n.StructureStatus, n.StructureFailure, n.SignedAt, n.SignedByCrp, n.ExportedAt,
                              ProviderHasCrp = db.Providers.Any(pr => pr.Id == n.ProviderId && pr.CrpNumber != null && pr.CrpRegion != null),
                              SectionsByAi = db.AuditEntries
                                  .Where(a => a.NoteId == n.Id && (a.Action == "ai_generated" || a.Action == "sections_created"))
                                  .OrderByDescending(a => a.At).ThenByDescending(a => a.Action == "ai_generated")
                                  .Select(a => a.Action)
                                  .FirstOrDefault() == "ai_generated",
                          }).SingleOrDefaultAsync(ct);
        if (note is null)
        {
            return null;
        }

        var sections = await db.NoteSections.AsNoTracking()
            .Where(s => s.NoteId == noteId)
            .OrderBy(s => s.Position)
            .Select(s => new NoteSectionDto(s.Key, s.Content, s.Position, s.GeneratedAt, s.EditedAt, s.Revision))
            .ToListAsync(ct);

        return new NoteDetail(note.Id, note.PatientId, note.PatientName, note.Status, note.InputMode, note.Format,
            note.RawTranscript, note.FailureReason, note.CreatedAt, note.StructureStatus, note.StructureFailure, sections,
            note.SignedAt, note.SignedByCrp, note.ProviderHasCrp, note.SectionsByAi, note.ExportedAt);
    }
}
