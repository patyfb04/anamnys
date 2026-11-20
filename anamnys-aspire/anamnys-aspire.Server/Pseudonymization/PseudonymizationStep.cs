using Anamnys.Server.Data;
using Anamnys.Server.Notes;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Pseudonymization;

public enum PseudonymizationOutcome { Done, Failed, Gone }

// The pipeline step before RawTranscript is written (design/specs/2026-11-17-pseudonymization-design.md §2), for a note in
// Processing: the live transcript, the text of "Nota com IA" and a retry. Done: the pseudonymised text is the transcript,
// TranscriptPseudonymized is set, the note is ReadyForReview and transcript_pseudonymized is audited with counts only.
// Failed (model error, a model run over its time budget, invalid output): nothing with names is written; the note is Failed with
// pseudonymization_failed and the original text is held in memory (PseudonymizationHolds) for a retry. Gone: the note
// left Processing meanwhile (deleted or discarded); nothing is written or held. Logs carry ids and type names only.
public sealed class PseudonymizationStep(
    Pseudonymizer pseudonymizer, PseudonymizationHolds holds, TimeProvider clock, ILogger<PseudonymizationStep> logger)
{
    public const string FailureReason = "pseudonymization_failed";
    public const string DoneAction = "transcript_pseudonymized";
    public const string FailedAction = "pseudonymization_failed";

    public PseudonymizationHolds Holds => holds;

    // heldUntil: the deadline of an earlier failure (a retry keeps it).
    public async Task<PseudonymizationOutcome> RunAsync(
        AnamnysDbContext db, Guid noteId, Guid providerId, Guid patientId, string text, CancellationToken ct, DateTimeOffset? heldUntil = null)
    {
        PseudonymizationResult result;
        try
        {
            // Time budgets are per model run, once the gate is held (LlamaThirdPartyDetector.RunTimeout): a long transcript
            // is many runs and waiting for the gate never counts. A run over its budget surfaces here as TimeoutException.
            result = await pseudonymizer.PseudonymizeAsync(db, providerId, patientId, text, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Exception type only: a message could echo the text.
            logger.LogError("Pseudonymization failed for note {NoteId}: {ExceptionType}", noteId, e.GetType().Name);
            db.ChangeTracker.Clear();
            if (!await FailAsync(db, noteId, providerId, clock.GetUtcNow(), ct))
            {
                return PseudonymizationOutcome.Gone;
            }
            holds.Hold(noteId, providerId, patientId, text, heldUntil);
            return PseudonymizationOutcome.Failed;
        }

        var now = clock.GetUtcNow();
        var written = false;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            written = await db.Notes
                .Where(n => n.Id == noteId && n.ProviderId == providerId && n.Status == "Processing")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.RawTranscript, result.Text)
                    .SetProperty(n => n.TranscriptPseudonymized, true)
                    .SetProperty(n => n.Status, "ReadyForReview")
                    .SetProperty(n => n.PseudonymizationFailedAt, (DateTimeOffset?)null)
                    .SetProperty(n => n.UpdatedAt, now), ct) > 0;
            if (!written)
            {
                return;
            }
            NoteAudit.Add(db, noteId, null, DoneAction, "transcript", null, result.AuditValue, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return written ? PseudonymizationOutcome.Done : PseudonymizationOutcome.Gone;
    }

    private static async Task<bool> FailAsync(AnamnysDbContext db, Guid noteId, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        var failed = false;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            failed = await db.Notes
                .Where(n => n.Id == noteId && n.ProviderId == providerId && n.Status == "Processing")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.Status, "Failed")
                    .SetProperty(n => n.FailureReason, FailureReason)
                    .SetProperty(n => n.StructureStatus, (string?)null)
                    .SetProperty(n => n.PseudonymizationFailedAt, now)
                    .SetProperty(n => n.UpdatedAt, now), ct) > 0;
            if (!failed)
            {
                return;
            }
            NoteAudit.Add(db, noteId, null, FailedAction, "transcript", null, null, now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return failed;
    }
}
