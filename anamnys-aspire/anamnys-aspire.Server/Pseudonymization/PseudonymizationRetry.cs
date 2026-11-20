using Anamnys.Server.Data;
using Anamnys.Server.LiveSession;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Pseudonymization;

public enum RetryOutcome { Accepted, NotFound, NotFailed, Gone }

// "Tentar de novo" for a note whose pseudonymisation failed (design/specs/2026-11-17-pseudonymization-design.md §2): while
// its original text is still held in memory, the note goes back to Processing and the step runs again in the worker
// (then structuring). After the hold expired or a restart, the text is gone (410) and the note stays failed.
public static class PseudonymizationRetry
{
    public static async Task<RetryOutcome> RequestAsync(
        AnamnysDbContext db, PseudonymizationHolds holds, TranscriptionQueue queue, Guid providerId, Guid noteId,
        DateTimeOffset now, CancellationToken ct)
    {
        var note = await db.Notes.AsNoTracking()
            .Where(n => n.Id == noteId && n.ProviderId == providerId)
            .Select(n => new { n.Status, n.FailureReason })
            .SingleOrDefaultAsync(ct);
        if (note is null)
        {
            return RetryOutcome.NotFound;
        }
        if (note.Status != "Failed" || note.FailureReason != PseudonymizationStep.FailureReason)
        {
            return RetryOutcome.NotFailed;
        }
        if (holds.Take(noteId, providerId) is not { } held)
        {
            return RetryOutcome.Gone;
        }

        // From here the text lives only in this call: the caller's token is not used for the write.
        int updated;
        try
        {
            updated = await db.Notes
                .Where(n => n.Id == noteId && n.ProviderId == providerId && n.Status == "Failed"
                    && n.FailureReason == PseudonymizationStep.FailureReason)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.Status, "Processing")
                    .SetProperty(n => n.FailureReason, (string?)null)
                    .SetProperty(n => n.StructureStatus, "pending")
                    .SetProperty(n => n.PseudonymizationFailedAt, (DateTimeOffset?)null)
                    .SetProperty(n => n.UpdatedAt, now), CancellationToken.None);
        }
        catch
        {
            // The write failed: the text stays retryable with its first deadline.
            holds.Hold(noteId, providerId, held.PatientId, held.Text, held.Until);
            throw;
        }
        if (updated == 0)
        {
            // Another retry won the race: the text goes back to the hold with its deadline.
            holds.Hold(noteId, providerId, held.PatientId, held.Text, held.Until);
            return RetryOutcome.NotFailed;
        }
        queue.Enqueue(new PseudonymizeJob(noteId, providerId, held.PatientId, held.Text, held.Until));
        return RetryOutcome.Accepted;
    }
}

public static class PseudonymizationEndpoints
{
    public static void MapPseudonymizationEndpoints(this RouteGroupBuilder phi)
    {
        // 202 accepted; 404 unknown or another provider's note; 409 the note is not failed by pseudonymisation; 410 its text is gone.
        phi.MapPost("providers/me/notes/{noteId:guid}/pseudonymization/retry", async (Guid noteId, HttpContext http,
            AnamnysDbContext db, PseudonymizationHolds holds, TranscriptionQueue queue, TimeProvider clock, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await PseudonymizationRetry.RequestAsync(db, holds, queue, providerId, noteId, clock.GetUtcNow(), ct) switch
            {
                RetryOutcome.Accepted => Results.Accepted(),
                RetryOutcome.NotFound => Results.NotFound(),
                RetryOutcome.Gone => Results.Json(new { message = "O texto original não está mais disponível. Dite novamente ou escreva a nota.", code = "text_gone" },
                    statusCode: StatusCodes.Status410Gone),
                _ => Results.Conflict(new { message = "A nota não está aguardando nova tentativa.", code = "not_failed" }),
            };
        });
    }
}
