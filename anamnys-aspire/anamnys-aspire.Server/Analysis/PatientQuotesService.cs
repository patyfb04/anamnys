using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Excerpts;
using Anamnys.Server.Notes;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Analysis;

public enum QuoteOutcome { Ok, NotFound, Archived, NoteNotReady, InvalidSelection, TermInactive }

public sealed record QuoteResult(QuoteOutcome Outcome, PatientQuoteItem? Quote = null);

// The selection in the note view (design/specs/2026-10-30-patient-quotes-design.md §3). Quote is the text the client saw: it
// only confirms the offsets still point at it (spacing aside); what is stored is the passage of the note. Body only.
public sealed record RecordQuoteRequest(string? SectionKey, int? OffsetStart, int? OffsetEnd, string? Quote, Guid? TermId = null)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(SectionKey) && OffsetStart is not null && OffsetEnd is not null
        && !string.IsNullOrWhiteSpace(Quote) && Quote.Length <= ExcerptQueries.MaxQuoteLength;
}

// Quote writes. A note or quote of another provider (or of a discarded record, F-06) is NotFound; an archived record is
// read-only (Archived). Every change is audited with ids only, in the transaction that makes it; a repeated removal is a
// no-op (no audit). Only what the provider marks is a quote: nothing here infers one.
public static class PatientQuotesService
{
    public const string QuoteRecorded = "quote_recorded";
    public const string QuoteRemoved = "quote_removed";

    public static async Task<QuoteResult> RecordAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, RecordQuoteRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        var result = new QuoteResult(QuoteOutcome.NotFound);
        await PlanAdherenceService.InTransactionAsync(db, async () =>
        {
            await NoteLocks.LockPatientAsync(db, providerId, noteId, ct);
            // The note lock keeps the passage valid against a concurrent section save (ExcerptService contract).
            await NoteLocks.LockAsync(db, noteId, ct);
            var note = await PlanAdherenceService.NoteAsync(db, providerId, noteId, ct);
            if (note is null)
            {
                result = new QuoteResult(QuoteOutcome.NotFound);
                return false;
            }
            if (note.Archived)
            {
                result = new QuoteResult(QuoteOutcome.Archived);
                return false;
            }
            if (!PlanAdherenceService.MarkableStatuses.Contains(note.Status))
            {
                result = new QuoteResult(QuoteOutcome.NoteNotReady);
                return false;
            }
            PatientQuoteTerm? term = null;
            if (request.TermId is { } termId)
            {
                var label = await (
                    from t in db.ThemeTerms.AsNoTracking()
                    join v in db.ThemeVocabularies.AsNoTracking() on t.VocabularyId equals v.Id
                    where t.Id == termId && v.Active && t.Status == "active"
                    select t.Label)
                    .SingleOrDefaultAsync(ct);
                if (label is null)
                {
                    result = new QuoteResult(QuoteOutcome.TermInactive);
                    return false;
                }
                term = new PatientQuoteTerm(termId, label);
            }

            var reference = await ExcerptService.AddAsync(db, providerId, noteId, request.SectionKey, null, request.OffsetStart, request.OffsetEnd, ct);
            if (reference.Outcome != ExcerptOutcome.Ok || reference.Ref is not { Kind: "exact" } passage || Collapsed(passage.Quote) != Collapsed(request.Quote))
            {
                result = new QuoteResult(QuoteOutcome.InvalidSelection);
                return false;
            }

            var quote = new PatientQuote
            {
                Id = Guid.NewGuid(),
                NoteId = noteId,
                TermId = term?.TermId,
                Text = passage.Quote!,
                ExcerptRefId = passage.Id,
                RecordedAt = nowUtc,
                AddedById = providerId,
            };
            db.PatientQuotes.Add(quote);
            PlanAdherenceService.Audit(db, noteId, providerId, QuoteRecorded, "quote", quote.Id.ToString(), nowUtc);
            await db.SaveChangesAsync(ct);
            var dated = await CaseAnalysisQuery.DatedNotesAsync(db, providerId, note.PatientId, signedOnly: false, ct);
            result = new QuoteResult(QuoteOutcome.Ok, new PatientQuoteItem(
                quote.Id, quote.Text, dated.Single(n => n.NoteId == noteId).On, noteId, passage.Id, passage.SectionKey, term));
            return true;
        }, ct);
        return result;
    }

    public static async Task<QuoteOutcome> RemoveAsync(
        AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        var outcome = QuoteOutcome.NotFound;
        await PlanAdherenceService.InTransactionAsync(db, async () =>
        {
            var noteId = await db.PatientQuotes.AsNoTracking()
                .RequireExcerpt(q => q.ExcerptRefId)
                .Where(q => q.Id == id)
                .Select(q => (Guid?)q.NoteId)
                .SingleOrDefaultAsync(ct);
            var note = noteId is { } owner ? await PlanAdherenceService.NoteAsync(db, providerId, owner, ct) : null;
            if (note is null)
            {
                outcome = QuoteOutcome.NotFound;
                return false;
            }
            // The note lock serialises two removals of a quote and a concurrent discard.
            await NoteLocks.LockAsync(db, noteId!.Value, ct);
            var quote = await db.PatientQuotes.SingleOrDefaultAsync(q => q.Id == id, ct);
            if (quote is null)
            {
                outcome = QuoteOutcome.NotFound;
                return false;
            }
            if (note.Archived)
            {
                outcome = QuoteOutcome.Archived;
                return false;
            }
            outcome = QuoteOutcome.Ok;
            if (quote.RemovedAt is not null)
            {
                return false;
            }
            quote.RemovedAt = nowUtc;
            quote.RemovedById = providerId;
            PlanAdherenceService.Audit(db, quote.NoteId, providerId, QuoteRemoved, "quote", quote.Id.ToString(), nowUtc);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return outcome;
    }

    private static string Collapsed(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
