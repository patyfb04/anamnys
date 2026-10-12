using Anamnys.Server.Data;
using Anamnys.Server.LiveSession;
using Anamnys.Server.NoteStructuring;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

public sealed record RestructureRequest(string Format);

public enum RestructureOutcome { Accepted, NotFound, Conflict, Invalid }

public static class NoteRestructure
{
    // The single conditional update is the guard: only a reviewable, unsigned note with a transcript,
    // no structuring in flight and no section edited by the provider is flipped to pending.
    public static async Task<RestructureOutcome> RequestAsync(
        AnamnysDbContext db, TranscriptionQueue queue, Guid providerId, Guid noteId, string format,
        DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(format) || !NoteFormats.IsStructurable(format))
        {
            return RestructureOutcome.Invalid;
        }
        if (!await db.Notes.AnyAsync(n => n.Id == noteId && n.ProviderId == providerId, ct))
        {
            return RestructureOutcome.NotFound;
        }

        var updated = await db.Notes
            .Where(n => n.Id == noteId && n.ProviderId == providerId
                && n.Status == "ReadyForReview"
                && n.SignedAt == null
                && n.RawTranscript != null && n.RawTranscript != ""
                && (n.StructureStatus == null || n.StructureStatus != "pending")
                && !db.NoteSections.Any(s => s.NoteId == n.Id && s.EditedAt != null))
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Format, format)
                .SetProperty(n => n.StructureStatus, "pending")
                .SetProperty(n => n.StructureFailure, (string?)null)
                .SetProperty(n => n.UpdatedAt, now), ct);
        if (updated == 0)
        {
            return RestructureOutcome.Conflict;
        }

        queue.Enqueue(new StructureJob(noteId, providerId));
        return RestructureOutcome.Accepted;
    }
}
