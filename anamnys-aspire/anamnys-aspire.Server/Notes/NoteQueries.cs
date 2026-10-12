using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

public sealed record NoteSectionDto(string Key, string Content, int Position, DateTimeOffset GeneratedAt, DateTimeOffset? EditedAt);

public sealed record NoteDetail(
    Guid Id, Guid PatientId, string PatientName, string Status, string InputMode, string Format,
    string? RawTranscript, string? FailureReason, DateTimeOffset CreatedAt,
    string? StructureStatus, string? StructureFailure, IReadOnlyList<NoteSectionDto> Sections);

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
                              n.RawTranscript, n.FailureReason, n.CreatedAt, n.StructureStatus, n.StructureFailure,
                          }).SingleOrDefaultAsync(ct);
        if (note is null)
        {
            return null;
        }

        var sections = await db.NoteSections.AsNoTracking()
            .Where(s => s.NoteId == noteId)
            .OrderBy(s => s.Position)
            .Select(s => new NoteSectionDto(s.Key, s.Content, s.Position, s.GeneratedAt, s.EditedAt))
            .ToListAsync(ct);

        return new NoteDetail(note.Id, note.PatientId, note.PatientName, note.Status, note.InputMode, note.Format,
            note.RawTranscript, note.FailureReason, note.CreatedAt, note.StructureStatus, note.StructureFailure, sections);
    }
}
