using Anamnys.Server.Data;
using Anamnys.Server.Excerpts;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Analysis;

public sealed record PatientQuoteTerm(Guid TermId, string Label);

// On: the note's day by the F-10 session date rule (practice-local). Section: the key of the section the reference points at.
public sealed record PatientQuoteItem(Guid Id, string Text, DateOnly On, Guid NoteId, Guid ExcerptId, string? Section, PatientQuoteTerm? Term);

public sealed record PatientQuotePage(IReadOnlyList<PatientQuoteItem> Items, int Total, int Page, int PageSize);

// The quotes the provider recorded (design/specs/2026-10-30-patient-quotes-design.md §3): active ones with their reference
// (RequireExcerpt, the hard rule of F-03). The patient list has signed notes only, by the session date rule (CaseAnalysisQuery),
// in chronological order; a theme filter includes the term's descendants at any depth. A signed note is never misquoted:
// the patient list shows a quote only while its exact text is in a section of its note (a passage edited away during
// review is left out; the note view is not filtered). Never interprets the text.
public static class PatientQuotesQuery
{
    public const int PageSize = 12;

    // Null for another provider's or a discarded patient. A page below 1 is the first; past the end, an empty page.
    public static async Task<PatientQuotePage?> ListAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateOnly? from, DateOnly? to, Guid? termId, int? page, CancellationToken ct)
    {
        if (!await db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null, ct))
        {
            return null;
        }
        var notes = (await CaseAnalysisQuery.DatedNotesAsync(db, providerId, patientId, signedOnly: true, ct))
            .Where(n => (from == null || n.On >= from) && (to == null || n.On <= to))
            .ToList();
        var terms = termId is { } id ? await WithDescendantsAsync(db, id, ct) : null;
        var rows = await ActiveAsync(db, providerId, notes, terms, inTextOnly: true, ct);
        var number = Math.Max(page ?? 1, 1);
        var items = rows.Skip((int)Math.Min(rows.Count, (number - 1L) * PageSize)).Take(PageSize).ToList();
        return new PatientQuotePage(items, rows.Count, number, PageSize);
    }

    // The note's active quotes, also in review, whether or not the text is still there: the provider can see and remove them.
    // Null for another provider's note or one of a discarded patient.
    public static async Task<IReadOnlyList<PatientQuoteItem>?> ForNoteAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct)
    {
        if (await PlanAdherenceService.NoteAsync(db, providerId, noteId, ct) is not { } note)
        {
            return null;
        }
        var dated = await CaseAnalysisQuery.DatedNotesAsync(db, providerId, note.PatientId, signedOnly: false, ct);
        return await ActiveAsync(db, providerId, dated.Where(n => n.NoteId == noteId).ToList(), null, inTextOnly: false, ct);
    }

    // Ordered by the notes' order, then by recording.
    private static async Task<List<PatientQuoteItem>> ActiveAsync(
        AnamnysDbContext db, Guid providerId, List<DatedNote> notes, IReadOnlyCollection<Guid>? terms, bool inTextOnly, CancellationToken ct)
    {
        var noteIds = notes.Select(n => n.NoteId).ToList();
        var active = db.PatientQuotes.AsNoTracking()
            .RequireExcerpt(q => q.ExcerptRefId)
            .Where(q => q.RemovedAt == null && noteIds.Contains(q.NoteId));
        if (terms is not null)
        {
            active = active.Where(q => q.TermId != null && terms.Contains(q.TermId.Value));
        }
        if (inTextOnly)
        {
            active = active.Where(q => db.NoteSections.Any(s => s.NoteId == q.NoteId && s.Content.Contains(q.Text)));
        }
        var rows = await (
            from q in active
            join r in db.ExcerptRefs.AsNoTracking().Where(r => r.ProviderId == providerId && r.SourceType == "note")
                on new { Id = q.ExcerptRefId, Note = q.NoteId } equals new { Id = (Guid?)r.Id, Note = r.SourceId }
            join t in db.ThemeTerms.AsNoTracking() on q.TermId equals (Guid?)t.Id into linked
            from t in linked.DefaultIfEmpty()
            select new { q.Id, q.Text, q.NoteId, q.RecordedAt, ExcerptId = r.Id, r.SectionKey, TermId = (Guid?)t.Id, Label = t == null ? null : t.Label })
            .ToListAsync(ct);

        var position = notes.Select((n, i) => (n, i)).ToDictionary(x => x.n.NoteId, x => (x.i, x.n.On));
        return rows
            .OrderBy(q => position[q.NoteId].i).ThenBy(q => q.RecordedAt).ThenBy(q => q.Id)
            .Select(q => new PatientQuoteItem(
                q.Id, q.Text, position[q.NoteId].On, q.NoteId, q.ExcerptId, q.SectionKey,
                q.TermId is { } termId ? new PatientQuoteTerm(termId, q.Label!) : null))
            .ToList();
    }

    private static async Task<HashSet<Guid>> WithDescendantsAsync(AnamnysDbContext db, Guid termId, CancellationToken ct)
    {
        var all = new HashSet<Guid> { termId };
        var frontier = new List<Guid> { termId };
        while (frontier.Count > 0)
        {
            var parents = frontier;
            var children = await db.ThemeTerms.AsNoTracking()
                .Where(t => t.ParentTermId != null && parents.Contains(t.ParentTermId.Value))
                .Select(t => t.Id)
                .ToListAsync(ct);
            frontier = children.Where(all.Add).ToList();
        }
        return all;
    }
}
