using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notes;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Excerpts;

public enum ExcerptOutcome { Ok, NotFound, InvalidSelection }

public sealed record ExcerptCreation(ExcerptOutcome Outcome, ExcerptRef? Ref = null);

public sealed record ExcerptPassage(
    Guid Id, string Kind, Guid NoteId, Guid PatientId, DateTimeOffset? SessionStartsAt, DateTimeOffset DatedAt,
    string SectionKey, string SectionTitle, string SectionText, int? Start, int? End);

// References into note sections (design/specs/2026-10-24-excerpt-references-design.md §3). Create and
// relocate only add to the context: the caller saves, inside the transaction that holds the note lock.
public static class ExcerptService
{
    // Adds a reference into a note section of the provider. The caller passes at most one of:
    //  - a selection (start, end): bounds are checked against the current section text and the selected
    //    text becomes the quote (kind exact);
    //  - a quote (typically from the model): located in the section, kind exact when found, else section.
    // Without sectionKey the first non-empty section is used. NotFound when the note or the section is
    // not available (or the note has no text).
    public static async Task<ExcerptCreation> AddAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, string? sectionKey, string? quote, int? start, int? end,
        CancellationToken ct)
    {
        var note = await (
            from n in db.Notes.AsNoTracking()
            join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
            where n.Id == noteId && n.ProviderId == providerId && p.ProviderId == providerId
            select new { n.CreatedAt, n.SessionId })
            .SingleOrDefaultAsync(ct);
        if (note is null)
        {
            return new ExcerptCreation(ExcerptOutcome.NotFound);
        }
        var session = note.SessionId is { } sessionId
            ? await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId)
                .Select(s => new { s.ScheduledStart, s.ActualStart }).SingleOrDefaultAsync(ct)
            : null;
        var sections = await db.NoteSections.AsNoTracking()
            .Where(s => s.NoteId == noteId)
            .OrderBy(s => s.Position)
            .Select(s => new { s.Key, s.Content })
            .ToListAsync(ct);
        // Without a section, a quote picks the first section it is found in.
        var quoted = sectionKey is null && start is null && end is null
            ? sections.FirstOrDefault(s => Find(s.Content, quote) is not null)
            : null;
        var section = quoted ?? (sectionKey is null
            ? sections.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Content))
            : sections.FirstOrDefault(s => s.Key == sectionKey));
        if (section is null)
        {
            return new ExcerptCreation(ExcerptOutcome.NotFound);
        }

        var reference = new ExcerptRef
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            SourceType = "note",
            SourceId = noteId,
            SectionKey = section.Key,
            Kind = "section",
            DatedAt = session?.ActualStart ?? session?.ScheduledStart ?? note.CreatedAt,
        };
        if (start is not null || end is not null)
        {
            if (start is not { } from || end is not { } to
                || from < 0 || to <= from || to > section.Content.Length
                || to - from > ExcerptQueries.MaxQuoteLength
                || string.IsNullOrWhiteSpace(section.Content[from..to]))
            {
                return new ExcerptCreation(ExcerptOutcome.InvalidSelection);
            }
            if ((from > 0 && char.IsLowSurrogate(section.Content[from]) && char.IsHighSurrogate(section.Content[from - 1]))
                || (to < section.Content.Length && char.IsLowSurrogate(section.Content[to]) && char.IsHighSurrogate(section.Content[to - 1])))
            {
                return new ExcerptCreation(ExcerptOutcome.InvalidSelection);
            }
            MakeExact(reference, section.Content, new ExcerptSpan(from, to));
        }
        else if (Find(section.Content, quote) is { } span)
        {
            MakeExact(reference, section.Content, span);
        }
        db.ExcerptRefs.Add(reference);
        return new ExcerptCreation(ExcerptOutcome.Ok, reference);
    }

    // After a draft section save: every reference of the section that has a quote is located again in the
    // new text. Found -> exact with the new offsets; gone -> section (the quote is kept, so a later edit
    // that brings it back restores the exact range). Callers must not call this for signed notes.
    public static async Task RelocateSectionAsync(
        AnamnysDbContext db, Guid noteId, string sectionKey, string newContent, CancellationToken ct)
    {
        var refs = await db.ExcerptRefs
            .Where(r => r.SourceType == "note" && r.SourceId == noteId && r.SectionKey == sectionKey && r.Quote != null)
            .ToListAsync(ct);
        foreach (var reference in refs)
        {
            if (ExcerptLocator.Locate(newContent, reference.Quote) is { } span
                && span.End - span.Start <= ExcerptQueries.MaxQuoteLength)
            {
                MakeExact(reference, newContent, span);
            }
            else
            {
                reference.Kind = "section";
                reference.OffsetStart = null;
                reference.OffsetEnd = null;
            }
        }
    }

    // After a restructure replaced the sections of a draft: every reference of the note is located again
    // (its quote first in its own section, then in any); when the quote is gone or the section key no
    // longer exists, it becomes a section reference on the first non-empty section (the quote is kept).
    // Returns how many references the note has, so the caller can queue a note-wide locate.
    public static async Task<int> RelocateNoteAsync(
        AnamnysDbContext db, Guid noteId, IReadOnlyList<NoteSection> newSections, CancellationToken ct)
    {
        var refs = await db.ExcerptRefs
            .Where(r => r.SourceType == "note" && r.SourceId == noteId)
            .ToListAsync(ct);
        var fallback = newSections.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Content)) ?? newSections.FirstOrDefault();
        foreach (var reference in refs)
        {
            var found = false;
            if (reference.Quote is not null)
            {
                foreach (var section in newSections.OrderBy(s => s.Key == reference.SectionKey ? 0 : 1))
                {
                    if (ExcerptLocator.Locate(section.Content, reference.Quote) is { } span
                        && span.End - span.Start <= ExcerptQueries.MaxQuoteLength)
                    {
                        reference.SectionKey = section.Key;
                        MakeExact(reference, section.Content, span);
                        found = true;
                        break;
                    }
                }
            }
            if (found)
            {
                continue;
            }
            reference.Kind = "section";
            reference.OffsetStart = null;
            reference.OffsetEnd = null;
            if (fallback is not null && (reference.Quote is not null || newSections.All(s => s.Key != reference.SectionKey)))
            {
                reference.SectionKey = fallback.Key;
            }
        }
        return refs.Count;
    }

    // Provider-scoped through the reference, the note and the patient; null for anything else.
    public static async Task<ExcerptPassage?> GetPassageAsync(AnamnysDbContext db, Guid providerId, Guid id, CancellationToken ct)
    {
        var row = await (
            from r in db.ExcerptRefs.AsNoTracking()
            join n in db.Notes.AsNoTracking() on r.SourceId equals n.Id
            join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
            where r.Id == id && r.SourceType == "note" && r.ProviderId == providerId
                  && n.ProviderId == providerId && p.ProviderId == providerId
            select new { r.Kind, r.Quote, r.SectionKey, r.OffsetStart, r.OffsetEnd, r.DatedAt, NoteId = n.Id, PatientId = p.Id, n.SessionId })
            .SingleOrDefaultAsync(ct);
        if (row?.SectionKey is not { } key)
        {
            return null;
        }
        var text = await db.NoteSections.AsNoTracking()
            .Where(s => s.NoteId == row.NoteId && s.Key == key)
            .Select(s => s.Content)
            .SingleOrDefaultAsync(ct);
        if (text is null)
        {
            return null;
        }
        var session = row.SessionId is { } sessionId
            ? await db.Sessions.AsNoTracking().Where(s => s.Id == sessionId)
                .Select(s => new { s.ScheduledStart, s.ActualStart }).SingleOrDefaultAsync(ct)
            : null;
        var exact = row.Kind == "exact" && row.OffsetStart is { } a && row.OffsetEnd is { } b && a >= 0 && b > a && b <= text.Length
            && string.Equals(text[a..b], row.Quote, StringComparison.Ordinal);
        return new ExcerptPassage(
            id, exact ? "exact" : "section", row.NoteId, row.PatientId, session?.ActualStart ?? session?.ScheduledStart, row.DatedAt,
            key, NotePdfModel.SectionTitle(key), text, exact ? row.OffsetStart : null, exact ? row.OffsetEnd : null);
    }

    // "Localizar trechos": a section reference (or one whose quote was lost) becomes exact when the quote
    // is found in a section of its note (only its own section with onlyCurrentSection: the provider chose
    // it); the reference then points into that section. Adds to the context only; false when not found.
    public static async Task<bool> TryUpgradeAsync(AnamnysDbContext db, ExcerptRef reference, string? quote, CancellationToken ct, bool onlyCurrentSection = false)
    {
        if (string.IsNullOrWhiteSpace(quote) || reference.SourceType != "note")
        {
            return false;
        }
        var sections = await db.NoteSections.AsNoTracking()
            .Where(s => s.NoteId == reference.SourceId && (!onlyCurrentSection || s.Key == reference.SectionKey))
            .OrderBy(s => s.Position)
            .Select(s => new { s.Key, s.Content })
            .ToListAsync(ct);
        // The section it already points at first (it may be the one the provider chose), then the others in order.
        foreach (var section in sections.OrderBy(s => s.Key == reference.SectionKey ? 0 : 1))
        {
            if (Find(section.Content, quote) is { } span)
            {
                reference.SectionKey = section.Key;
                MakeExact(reference, section.Content, span);
                return true;
            }
        }
        return false;
    }

    // Short context around a reference for result lists (one line, at most max chars, "…" marks a cut):
    // centred on the range for an exact reference, the start of the section otherwise.
    public static string Snippet(string sectionText, int? start, int? end, int max = 160)
    {
        string Line(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (start is not { } a || end is not { } b || a < 0 || b <= a || b > sectionText.Length)
        {
            var whole = Line(sectionText);
            return whole.Length <= max ? whole : whole[..(max - 1)].TrimEnd() + "…";
        }
        if (b - a >= max - 2)
        {
            return Line(sectionText[a..Math.Min(b, a + max - 1)]) + (b - a > max - 1 ? "…" : "");
        }
        var room = max - 2 - (b - a);
        var from = Math.Max(0, a - room / 2);
        var to = Math.Min(sectionText.Length, b + (room - (a - from)));
        from = Math.Max(0, from - (room - (a - from) - (to - b)));
        return (from > 0 ? "…" : "") + Line(sectionText[from..to]) + (to < sectionText.Length ? "…" : "");
    }

    // The quote's range in a section text, also when the model copied the "Título: " the prompt shows
    // before each section; null when absent or longer than a quote may be.
    private static ExcerptSpan? Find(string text, string? quote)
    {
        if (string.IsNullOrWhiteSpace(quote))
        {
            return null;
        }
        var span = ExcerptLocator.Locate(text, quote);
        if (span is null && quote.IndexOf(':') is var colon and > 0 and < 20)
        {
            span = ExcerptLocator.Locate(text, quote[(colon + 1)..]);
        }
        return span is { } s && s.End - s.Start <= ExcerptQueries.MaxQuoteLength ? s : null;
    }

    private static void MakeExact(ExcerptRef reference, string sectionText, ExcerptSpan span)
    {
        reference.Kind = "exact";
        reference.OffsetStart = span.Start;
        reference.OffsetEnd = span.End;
        reference.Quote = sectionText[span.Start..span.End];
    }
}
