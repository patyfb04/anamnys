using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Excerpts;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Analysis;

public sealed record RecordSearchRequest(string? Question, DateOnly? From = null, DateOnly? To = null, int? Page = null)
{
    public const int MinLength = 3;
    public const int MaxLength = 300;

    // The messages never echo the question.
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Page is < 1)
        {
            errors["page"] = ["A página deve ser 1 ou maior."];
        }
        if (Question?.Trim().Length is not (>= MinLength and <= MaxLength))
        {
            errors["question"] = [$"A pergunta deve ter de {MinLength} a {MaxLength} caracteres."];
        }
        if (From > To)
        {
            errors["from"] = ["O início do período deve ser anterior ao fim."];
        }
        return errors;
    }
}

public sealed record RecordSearchTerm(Guid TermId, string Label);

// On: the note's day by the F-10 session date rule (practice-local). Section: the section key. Snippet: plain text, with
// Highlights as offsets into it (none on a theme passage). ExcerptId and Term: on theme passages only.
public sealed record RecordSearchPassage(
    Guid NoteId, DateOnly On, string Section, string Snippet, IReadOnlyList<RecordSearchHighlight> Highlights,
    Guid? ExcerptId, string MatchedBy, RecordSearchTerm? Term);

// Total: every passage found; Passages: page Page of PageSize passages in the given order.
public sealed record RecordSearchResult(IReadOnlyList<RecordSearchPassage> Passages, int Total, string Order, int Page, int PageSize);

// F-16 (design/specs/2026-10-31-record-search-design.md §3): passages of the patient's signed notes, never a synthesis.
// Text: a section with every word of the question, in full-text search (all of them: the closest passage is not an
// answer). Theme: counted tags of the terms named in the question and their descendants, only when the question
// names nothing else. A section with a theme passage does not repeat as a text passage: the theme one has the reference.
// A reference may keep a quote that left the note: a quote not found in the section (ExcerptLocator) shows the section instead.
// Fixed number of queries. The question is never logged or audited: it reaches Postgres as a bound parameter.
public static class RecordSearchQuery
{
    public const int PageSize = 5;
    public const string ByText = "text";
    public const string ByTheme = "theme";

    // Inline in the SQL: the expression must be the one of the index NoteSections_Content_fts_idx.
    private const string Config = "anamnys_pt";
    private const string HeadlineOptions = "HighlightAll=true,StartSel=" + RecordSearch.MarkStart + ",StopSel=" + RecordSearch.MarkEnd;

    private sealed record Hit(Guid NoteId, string Section, int Position, string? Snippet, Guid? ExcerptId, RecordSearchTerm? Term);

    // Null for another provider's or a discarded patient.
    public static async Task<RecordSearchResult?> ExecuteAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, string question, DateOnly? from, DateOnly? to, CancellationToken ct, int pageNumber = 1)
    {
        if (!await db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null, ct))
        {
            return null;
        }
        var parsed = RecordSearch.Parse(question);
        var notes = (await CaseAnalysisQuery.DatedNotesAsync(db, providerId, patientId, signedOnly: true, ct))
            .Where(n => (from == null || n.On >= from) && (to == null || n.On <= to))
            .ToList();
        var words = RecordSearch.TextQuery(parsed.Words);
        var noteIds = notes.Select(n => n.NoteId).ToList();

        var hits = await ThemeHitsAsync(db, providerId, parsed, noteIds, ct);
        if (words.Length > 0)
        {
            var themed = hits.Select(h => (h.NoteId, h.Section)).ToHashSet();
            var sections = await MatchingSections(db, noteIds, words)
                .Select(s => new { s.NoteId, s.Key, s.Position })
                .ToListAsync(ct);
            hits.AddRange(sections.Where(s => !themed.Contains((s.NoteId, s.Key))).Select(s => new Hit(s.NoteId, s.Key, s.Position, null, null, null)));
        }

        var position = notes.Select((n, i) => (n.NoteId, i)).ToDictionary(x => x.NoteId, x => x.i);
        var ordered = parsed.Order == RecordSearch.Latest ? hits.OrderByDescending(h => position[h.NoteId]) : hits.OrderBy(h => position[h.NoteId]);
        var page = ordered.ThenBy(h => h.Position).ThenBy(h => h.ExcerptId is null).ToList().Skip((pageNumber - 1) * PageSize).Take(PageSize).ToList();

        var marked = await MarkedAsync(db, page.Where(h => h.Snippet is null).ToList(), words, ct);
        var day = notes.ToDictionary(n => n.NoteId, n => n.On);
        var passages = page.Select(h =>
        {
            var (snippet, highlights) = h.Snippet is { } quote ? (quote, []) : RecordSearch.Snippet(marked[(h.NoteId, h.Section)]);
            return new RecordSearchPassage(
                h.NoteId, day[h.NoteId], h.Section, snippet, highlights, h.ExcerptId, h.ExcerptId is null ? ByText : ByTheme, h.Term);
        }).ToList();
        return new RecordSearchResult(passages, hits.Count, parsed.Order, pageNumber, PageSize);
    }

    // Sections with every word of textQuery (RecordSearch.TextQuery).
    internal static IQueryable<NoteSection> MatchingSections(AnamnysDbContext db, List<Guid> noteIds, string textQuery) =>
        db.NoteSections.AsNoTracking()
            .Where(s => noteIds.Contains(s.NoteId)
                && EF.Functions.ToTsVector(Config, s.Content).Matches(EF.Functions.ToTsQuery(Config, textQuery)));

    private static async Task<List<Hit>> ThemeHitsAsync(
        AnamnysDbContext db, Guid providerId, RecordSearchQuestion question, List<Guid> noteIds, CancellationToken ct)
    {
        var vocabulary = await db.ThemeTerms.AsNoTracking()
            .Select(t => new RecordSearchVocabularyTerm(t.Id, t.Code, t.Label, t.ParentTermId))
            .ToListAsync(ct);
        var match = RecordSearch.MatchTerms(question, vocabulary);
        if (!match.CoversQuestion)
        {
            return [];
        }
        var termIds = match.TermIds.ToList();
        var rows = await (
            from tag in CaseAnalysisQuery.CountedTags(db)
            join r in db.ExcerptRefs.AsNoTracking().Where(r => r.ProviderId == providerId && r.SourceType == "note")
                on new { Id = tag.ExcerptRefId, Note = tag.NoteId } equals new { Id = (Guid?)r.Id, Note = r.SourceId }
            join s in db.NoteSections.AsNoTracking()
                on new { Note = tag.NoteId, Key = r.SectionKey } equals new { Note = s.NoteId, Key = (string?)s.Key }
            where noteIds.Contains(tag.NoteId) && termIds.Contains(tag.TermId)
            orderby tag.CreatedAt, tag.Id
            select new { tag.NoteId, tag.TermId, ExcerptId = r.Id, r.Quote, s.Key, s.Position, s.Content })
            .ToListAsync(ct);

        var labels = vocabulary.ToDictionary(t => t.Id, t => t.Label);
        return rows
            .DistinctBy(r => r.ExcerptId)
            .Select(r => new Hit(
                r.NoteId, r.Key, r.Position, ExcerptLocator.Locate(r.Content, r.Quote) is null ? RecordSearch.Opening(r.Content) : r.Quote,
                r.ExcerptId, new RecordSearchTerm(r.TermId, labels[r.TermId])))
            .Where(h => !string.IsNullOrWhiteSpace(h.Snippet))
            .ToList();
    }

    // The text passages of the page only: a patient's whole history would travel otherwise.
    private static async Task<Dictionary<(Guid, string), string>> MarkedAsync(AnamnysDbContext db, List<Hit> hits, string words, CancellationToken ct)
    {
        if (hits.Count == 0)
        {
            return [];
        }
        var noteIds = hits.Select(h => h.NoteId).Distinct().ToList();
        var keys = hits.Select(h => h.Section).Distinct().ToList();
        var rows = await db.NoteSections.AsNoTracking()
            .Where(s => noteIds.Contains(s.NoteId) && keys.Contains(s.Key))
            .Select(s => new { s.NoteId, s.Key, Marked = EF.Functions.ToTsQuery(Config, words).GetResultHeadline(Config, s.Content, HeadlineOptions) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => (r.NoteId, r.Key), r => r.Marked);
    }
}
