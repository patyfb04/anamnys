using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Excerpts;
using Anamnys.Server.Notes;
using Anamnys.Server.Themes;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.NoteStructuring;

public enum StructuringOutcome { Done, Failed, Discarded, Skipped }

// Turns a note's transcript into sections (design/specs/2026-10-12-note-structuring-design.md §5).
// Never changes Notes.Status: a structuring failure leaves the transcript as the note.
// `format` is the restructure's requested format (null = the note's current one, as for dictation);
// it is written to Notes.Format only in the transaction that replaces the sections.
public static class NoteStructuringJob
{
    public static async Task<StructuringOutcome> ProcessAsync(
        AnamnysDbContext db, INoteStructurer structurer, IThemeSuggester themes, Guid noteId, Guid providerId,
        TimeProvider clock, ILogger logger, CancellationToken ct, string? format = null, ThemeLocateQueue? locates = null)
    {
        var note = await db.Notes.AsNoTracking()
            .Where(n => n.Id == noteId && n.ProviderId == providerId)
            .Select(n => new { n.RawTranscript, n.Format, n.StructureStatus, n.InputMode })
            .SingleOrDefaultAsync(ct);
        if (note is null || note.StructureStatus != "pending")
        {
            return StructuringOutcome.Skipped;
        }
        // A restructure brings the requested format; Notes.Format changes only with the new sections.
        var effectiveFormat = format ?? note.Format;
        if (string.IsNullOrWhiteSpace(note.RawTranscript) || !NoteFormats.IsStructurable(effectiveFormat))
        {
            await SetStatusAsync(db, noteId, providerId, null, null, clock.GetUtcNow(), ct);
            return StructuringOutcome.Skipped;
        }

        IReadOnlyList<StructuredSection> sections;
        try
        {
            sections = await structurer.StructureAsync(note.RawTranscript, effectiveFormat, NoteSources.Of(note.InputMode), ct);
        }
        catch (TranscriptTooLongException)
        {
            await SetStatusAsync(db, noteId, providerId, "failed", "too_long", clock.GetUtcNow(), ct);
            return StructuringOutcome.Failed;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError("Structuring failed for note {NoteId}: {ExceptionType}", noteId, e.GetType().Name);
            await SetStatusAsync(db, noteId, providerId, "failed", "model_error", clock.GetUtcNow(), ct);
            return StructuringOutcome.Failed;
        }

        if (!sections.Select(s => s.Key).SequenceEqual(NoteFormats.KeysFor(effectiveFormat)))
        {
            logger.LogError("Structuring for note {NoteId} returned unexpected sections.", noteId);
            await SetStatusAsync(db, noteId, providerId, "failed", "model_error", clock.GetUtcNow(), ct);
            return StructuringOutcome.Failed;
        }

        var now = clock.GetUtcNow();
        var outcome = StructuringOutcome.Done;
        var excerptRefs = 0;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            outcome = StructuringOutcome.Done;
            excerptRefs = 0;
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await NoteLocks.LockAsync(db, noteId, ct);
            if (await db.NoteSections.AnyAsync(s => s.NoteId == noteId && s.EditedAt != null, ct))
            {
                outcome = StructuringOutcome.Discarded;
            }
            else
            {
                await db.NoteSections.Where(s => s.NoteId == noteId).ExecuteDeleteAsync(ct);
                var rows = sections.Select((s, i) => new NoteSection
                {
                    Id = Guid.NewGuid(),
                    NoteId = noteId,
                    Key = s.Key,
                    Content = s.Content.Trim(),
                    Position = i,
                    GeneratedAt = now,
                }).ToList();
                db.NoteSections.AddRange(rows);
                excerptRefs = await ExcerptService.RelocateNoteAsync(db, noteId, rows, ct);
                foreach (var row in rows)
                {
                    NoteAudit.Add(db, noteId, null, "ai_generated", row.Key, null, row.Content, now);
                }
                await db.SaveChangesAsync(ct);
                await db.Notes
                    .Where(n => n.Id == noteId && n.ProviderId == providerId)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.Format, effectiveFormat), ct);
            }
            await SetStatusAsync(db, noteId, providerId, "done", null, now, ct);
            await tx.CommitAsync(ct);
        });
        // After the sections commit, in its own transaction; never changes the outcome.
        if (outcome == StructuringOutcome.Done && excerptRefs > 0)
        {
            locates?.Enqueue(new ThemeLocateJob(providerId, noteId));
        }
        if (outcome == StructuringOutcome.Done)
        {
            await ThemeProposals.ProposeAsync(db, themes, noteId, providerId, clock, logger, ct);
        }
        return outcome;
    }

    private static Task<int> SetStatusAsync(AnamnysDbContext db, Guid noteId, Guid providerId, string? status, string? failure, DateTimeOffset now, CancellationToken ct) =>
        db.Notes
            .Where(n => n.Id == noteId && n.ProviderId == providerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.StructureStatus, status)
                .SetProperty(n => n.StructureFailure, failure)
                .SetProperty(n => n.UpdatedAt, now), ct);
}
