using Anamnys.Server.Data;
using Anamnys.Server.Pseudonymization;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

// Title: set for an Anamnese note (F-31), from its intake template's section titles; null for the fixed formats.
public sealed record NoteSectionDto(string Key, string Content, int Position, DateTimeOffset GeneratedAt, DateTimeOffset? EditedAt, int Revision, string? Title = null);

public sealed record NoteDetail(
    Guid Id, Guid PatientId, string PatientName, string Status, string InputMode, string Format,
    string? RawTranscript, string? FailureReason, DateTimeOffset CreatedAt,
    string? StructureStatus, string? StructureFailure, IReadOnlyList<NoteSectionDto> Sections,
    DateTimeOffset? SignedAt, string? SignedByCrp, bool ProviderHasCrp, bool SectionsByAi,
    DateTimeOffset? ExportedAt, Guid? SessionId, Guid? IntakeId = null,
    DateTimeOffset? AudioDestroyedAt = null, string? AudioOutcome = null, IReadOnlyList<string>? AudioSources = null,
    bool TranscriptPseudonymized = false, DateTimeOffset? PseudonymizationFailedAt = null, bool PseudonymizationRetryable = false);

public static class NoteQueries
{
    // holds: when given, PseudonymizationRetryable says whether the failed note's text is still in memory (F-27).
    public static async Task<NoteDetail?> GetAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct, PseudonymizationHolds? holds = null)
    {
        var note = await (from n in db.Notes.AsNoTracking()
                          join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
                          where n.Id == noteId && n.ProviderId == providerId
                          select new
                          {
                              n.Id, n.PatientId, PatientName = p.FirstName + " " + p.LastName, n.Status, n.InputMode, n.Format,
                              n.RawTranscript, n.FailureReason, n.CreatedAt, n.StructureStatus, n.StructureFailure, n.SignedAt, n.SignedByCrp, n.ExportedAt, n.SessionId, n.IntakeId,
                              n.TranscriptPseudonymized, n.PseudonymizationFailedAt,
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
        if (await Intakes.IntakeNote.SectionTitlesAsync(db, providerId, note.IntakeId, ct) is { } titles)
        {
            sections = [.. sections.Select(s => s with { Title = titles.GetValueOrDefault(s.Key) })];
        }

        // F-26: the latest destruction record of the note's recording (none for a typed note or one from before F-26).
        var audio = await db.AudioDestructionLogs.AsNoTracking()
            .Where(a => a.NoteId == noteId && a.ProviderId == providerId)
            .OrderByDescending(a => a.DestroyedAt)
            .Select(a => new { a.DestroyedAt, a.Outcome, a.Sources })
            .FirstOrDefaultAsync(ct);

        return new NoteDetail(note.Id, note.PatientId, note.PatientName, note.Status, note.InputMode, note.Format,
            note.RawTranscript, note.FailureReason, note.CreatedAt, note.StructureStatus, note.StructureFailure, sections,
            note.SignedAt, note.SignedByCrp, note.ProviderHasCrp, note.SectionsByAi, note.ExportedAt, note.SessionId, note.IntakeId,
            audio?.DestroyedAt, audio?.Outcome, audio?.Sources,
            note.TranscriptPseudonymized, note.PseudonymizationFailedAt,
            note.Status == "Failed" && note.FailureReason == PseudonymizationStep.FailureReason && holds?.IsHeld(noteId, providerId) == true);
    }
}
