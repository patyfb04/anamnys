using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

public sealed record NoteDetail(
    Guid Id, Guid PatientId, string PatientName, string Status, string InputMode, string Format,
    string? RawTranscript, string? FailureReason, DateTimeOffset CreatedAt);

public static class NoteQueries
{
    public static Task<NoteDetail?> GetAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct) =>
        (from n in db.Notes.AsNoTracking()
         join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
         where n.Id == noteId && n.ProviderId == providerId
         select new NoteDetail(n.Id, n.PatientId, p.FirstName + " " + p.LastName, n.Status, n.InputMode, n.Format,
             n.RawTranscript, n.FailureReason, n.CreatedAt))
        .SingleOrDefaultAsync(ct);
}
