using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Excerpts;
using Anamnys.Server.Notes;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Themes;

public enum ThemeOutcome { Ok, NotFound, Archived, TermInactive, AlreadyTagged, InvalidSelection }

// ExcerptKind: the kind of the reference an add created (null: none, the note has no sections).
public sealed record ThemeResult(ThemeOutcome Outcome, Guid? Id = null, string? ExcerptKind = null);

public enum ThemeSuggestOutcome { Ok, NotFound, Failed }

public sealed record ThemeSuggestion(ThemeSuggestOutcome Outcome, IReadOnlyList<ThemeTermOption> Terms);

public enum ThemeLocateOutcome { Ok, NotFound, Archived, Failed }

// Located: references upgraded to exact by this call; Remaining: active tags still without an exact one.
public sealed record ThemeLocateResult(ThemeLocateOutcome Outcome, int Located = 0, int Remaining = 0);

// Where a tag points (design/specs/2026-10-24-excerpt-references-design.md §3): a selection in a section
// (SectionKey + Start/End), a whole section (SectionKey), or a quote from a suggestion. All null: the
// first non-empty section.
public sealed record ThemeAnchor(string? SectionKey = null, int? Start = null, int? End = null, string? Quote = null);

public sealed record NoteThemeItem(
    Guid Id, Guid TermId, string Code, string Label, string? ParentLabel, string TermStatus,
    string Origin, bool Proposed, DateTimeOffset AddedAt, Guid? ExcerptId = null, string? ExcerptKind = null);

// Themes are metadata over the note, not part of the signed text: signed notes can be tagged too.
public static class NoteThemeService
{
    public static async Task<IReadOnlyList<NoteThemeItem>?> ListAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct)
    {
        if (!await (
            from n in db.Notes.AsNoTracking()
            join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
            where n.Id == noteId && n.ProviderId == providerId && p.ProviderId == providerId
            select n.Id).AnyAsync(ct))
        {
            return null;
        }
        var rows = await (
            from tag in db.ThemeTags.AsNoTracking()
            join term in db.ThemeTerms.AsNoTracking() on tag.TermId equals term.Id
            join r in db.ExcerptRefs.AsNoTracking().Where(r => r.ProviderId == providerId) on tag.ExcerptRefId equals r.Id into refs
            from r in refs.DefaultIfEmpty()
            where tag.NoteId == noteId && tag.RemovedAt == null
            select new { tag, term, RefId = (Guid?)r.Id, RefKind = r.Kind })
            .ToListAsync(ct);
        var parentIds = rows.Where(r => r.term.ParentTermId != null).Select(r => r.term.ParentTermId!.Value).Distinct().ToList();
        var parents = await db.ThemeTerms.AsNoTracking()
            .Where(t => parentIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Label, ct);

        return rows
            .OrderBy(r => r.tag.CreatedAt).ThenBy(r => r.term.Label, StringComparer.CurrentCulture)
            .Select(r => new NoteThemeItem(
                r.tag.Id, r.term.Id, r.term.Code, r.term.Label,
                r.term.ParentTermId is { } parent ? parents.GetValueOrDefault(parent) : null,
                r.term.Status, r.tag.CreatedBy, r.tag.CreatedBy == "ai" && r.tag.AddedById is null, r.tag.CreatedAt,
                r.RefId, r.RefId is null ? null : r.RefKind))
            .ToList();
    }

    public static Task<ThemeResult> AddAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, Guid termId, bool fromSuggestion,
        DateTimeOffset now, CancellationToken ct) =>
        AddAsync(db, providerId, noteId, termId, fromSuggestion, null, now, ct);

    public static Task<ThemeResult> AddAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, Guid termId, bool fromSuggestion, ThemeAnchor? anchor,
        DateTimeOffset now, CancellationToken ct) =>
        InTransactionAsync(db, async () =>
        {
            await NoteLocks.LockAsync(db, noteId, ct);
            var archived = await PatientArchivedAsync(db, providerId, noteId, ct);
            if (archived is null)
            {
                return new ThemeResult(ThemeOutcome.NotFound);
            }
            if (archived.Value)
            {
                return new ThemeResult(ThemeOutcome.Archived);
            }
            var term = await (
                from t in db.ThemeTerms.AsNoTracking()
                join v in db.ThemeVocabularies.AsNoTracking() on t.VocabularyId equals v.Id
                where t.Id == termId && v.Active && t.Status == "active"
                select new { t.Id, t.Code })
                .SingleOrDefaultAsync(ct);
            if (term is null)
            {
                return new ThemeResult(ThemeOutcome.TermInactive);
            }
            if (await db.ThemeTags.AnyAsync(t => t.NoteId == noteId && t.TermId == termId && t.RemovedAt == null, ct))
            {
                return new ThemeResult(ThemeOutcome.AlreadyTagged);
            }

            // A selection or a chosen section must exist in the current text; without either, a note
            // with no sections (raw transcript only) keeps the tag without a reference.
            var hasSelection = anchor?.Start is not null || anchor?.End is not null;
            var reference = await ExcerptService.AddAsync(
                db, providerId, noteId, anchor?.SectionKey, hasSelection ? null : anchor?.Quote, anchor?.Start, anchor?.End, ct);
            if (reference.Outcome == ExcerptOutcome.InvalidSelection
                || (reference.Outcome == ExcerptOutcome.NotFound && anchor?.SectionKey is not null))
            {
                return new ThemeResult(ThemeOutcome.InvalidSelection);
            }

            var tag = new ThemeTag
            {
                Id = Guid.NewGuid(),
                NoteId = noteId,
                TermId = termId,
                ExcerptRefId = reference.Ref?.Id,
                CreatedBy = fromSuggestion ? "ai" : "human",
                CreatedAt = now,
                AddedById = providerId,
            };
            db.ThemeTags.Add(tag);
            NoteAudit.Add(db, noteId, providerId, "theme_added", "theme", null, term.Code, now);
            await db.SaveChangesAsync(ct);
            return new ThemeResult(ThemeOutcome.Ok, tag.Id, reference.Ref?.Kind);
        }, ct);

    public static Task<ThemeResult> RemoveAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, Guid tagId, DateTimeOffset now, CancellationToken ct) =>
        InTransactionAsync(db, async () =>
        {
            await NoteLocks.LockAsync(db, noteId, ct);
            var archived = await PatientArchivedAsync(db, providerId, noteId, ct);
            if (archived is null)
            {
                return new ThemeResult(ThemeOutcome.NotFound);
            }
            if (archived.Value)
            {
                return new ThemeResult(ThemeOutcome.Archived);
            }
            var tag = await db.ThemeTags.SingleOrDefaultAsync(t => t.Id == tagId && t.NoteId == noteId && t.RemovedAt == null, ct);
            if (tag is null)
            {
                return new ThemeResult(ThemeOutcome.NotFound);
            }
            var code = await db.ThemeTerms.AsNoTracking().Where(t => t.Id == tag.TermId).Select(t => t.Code).SingleAsync(ct);

            tag.RemovedAt = now;
            tag.RemovedById = providerId;
            NoteAudit.Add(db, noteId, providerId, "theme_removed", "theme", code, null, now);
            await db.SaveChangesAsync(ct);
            return new ThemeResult(ThemeOutcome.Ok, tag.Id);
        }, ct);

    // On-demand suggestions for any note; writes nothing. Terms already actively tagged cannot be
    // returned. Codes only (the fast mode, the provider waits): accepting one creates a section
    // reference and the background locate (ThemeLocateQueue) finds the passage. A model failure is
    // logged without the text.
    public static async Task<ThemeSuggestion> SuggestAsync(
        AnamnysDbContext db, IThemeSuggester suggester, Guid providerId, Guid noteId, ILogger logger, CancellationToken ct)
    {
        var note = await ThemeProposals.LoadNoteAsync(db, providerId, noteId, ct);
        if (note is null)
        {
            return new ThemeSuggestion(ThemeSuggestOutcome.NotFound, []);
        }
        if (string.IsNullOrWhiteSpace(note.Text))
        {
            return new ThemeSuggestion(ThemeSuggestOutcome.Ok, []);
        }
        var tagged = await db.ThemeTags.AsNoTracking()
            .Where(t => t.NoteId == noteId && t.RemovedAt == null)
            .Select(t => t.TermId)
            .ToListAsync(ct);
        // The model always sees the full active list (it is the cached prompt prefix); tagged terms
        // are dropped from its output.
        var all = await ThemeVocabularyQuery.ActiveTermOptionsAsync(db, ct);
        var options = all.Where(o => !tagged.Contains(o.Id)).ToList();
        if (options.Count == 0)
        {
            return new ThemeSuggestion(ThemeSuggestOutcome.Ok, []);
        }
        var excluded = all.Where(o => tagged.Contains(o.Id)).Select(o => o.Code).ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<ThemeCodeQuote> items;
        try
        {
            items = await suggester.SuggestAsync(note.Text, all, excluded, withQuotes: false, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError("Theme suggestion failed for note {NoteId}: {ExceptionType}", noteId, e.GetType().Name);
            return new ThemeSuggestion(ThemeSuggestOutcome.Failed, []);
        }
        var byCode = options.ToDictionary(o => o.Code, StringComparer.Ordinal);
        return new ThemeSuggestion(ThemeSuggestOutcome.Ok,
            items.Where(i => byCode.ContainsKey(i.Code)).DistinctBy(i => i.Code).Take(ThemeGrammar.MaxItems)
                .Select(i => byCode[i.Code]).ToList());
    }

    // "Localizar trechos": asks the model only for the quotes of the note's active tags whose reference
    // is not exact (or missing), and upgrades those it finds in the text. The model runs outside the
    // transaction; the tags are re-read under the note lock. Each upgrade is audited
    // (theme_excerpt_located, the term code). Works on drafts and signed notes (metadata only: the text
    // is not touched). tagId restricts the run to one tag (the background locate after an add); a tag
    // removed, or already exact, in the meantime is skipped, so a job is idempotent.
    public static async Task<ThemeLocateResult> LocateAsync(
        AnamnysDbContext db, IThemeSuggester suggester, Guid providerId, Guid noteId, DateTimeOffset now,
        ILogger logger, CancellationToken ct, Guid? tagId = null, bool sectionOnly = false)
    {
        var note = await ThemeProposals.LoadNoteAsync(db, providerId, noteId, ct);
        if (note is null)
        {
            return new ThemeLocateResult(ThemeLocateOutcome.NotFound);
        }
        if (note.Archived)
        {
            return new ThemeLocateResult(ThemeLocateOutcome.Archived);
        }
        var pending = await PendingAsync(db, providerId, noteId, tagId, ct);
        var all = await ThemeVocabularyQuery.ActiveTermOptionsAsync(db, ct);
        var codes = pending.Select(p => p.Code).Where(c => all.Any(o => o.Code == c)).Distinct(StringComparer.Ordinal).ToList();
        if (codes.Count == 0 || string.IsNullOrWhiteSpace(note.Text))
        {
            return new ThemeLocateResult(ThemeLocateOutcome.Ok, 0, pending.Count);
        }

        Dictionary<string, string> quotes;
        try
        {
            quotes = (await suggester.QuoteAsync(note.Text, all, codes, ct))
                .Where(q => q.Quote is not null && codes.Contains(q.Code))
                .DistinctBy(q => q.Code)
                .ToDictionary(q => q.Code, q => q.Quote!, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError("Theme excerpt location failed for note {NoteId}: {ExceptionType}", noteId, e.GetType().Name);
            return new ThemeLocateResult(ThemeLocateOutcome.Failed);
        }

        var result = new ThemeLocateResult(ThemeLocateOutcome.NotFound);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await NoteLocks.LockAsync(db, noteId, ct);
            var archived = await PatientArchivedAsync(db, providerId, noteId, ct);
            if (archived is not false)
            {
                result = new ThemeLocateResult(archived is null ? ThemeLocateOutcome.NotFound : ThemeLocateOutcome.Archived);
                return;
            }
            var current = await PendingAsync(db, providerId, noteId, tagId, ct);
            var located = 0;
            foreach (var item in current)
            {
                if (!quotes.TryGetValue(item.Code, out var quote))
                {
                    continue;
                }
                var tag = await db.ThemeTags.SingleAsync(t => t.Id == item.TagId, ct);
                var reference = tag.ExcerptRefId is { } refId
                    ? await db.ExcerptRefs.SingleOrDefaultAsync(r => r.Id == refId && r.ProviderId == providerId, ct)
                    : null;
                if (reference is not null)
                {
                    if (!await ExcerptService.TryUpgradeAsync(db, reference, quote, ct, sectionOnly))
                    {
                        continue;
                    }
                }
                else
                {
                    var created = await ExcerptService.AddAsync(db, providerId, noteId, null, quote, null, null, ct);
                    if (created.Ref is not { Kind: "exact" } exact)
                    {
                        if (created.Ref is not null)
                        {
                            db.ExcerptRefs.Remove(created.Ref);
                        }
                        continue;
                    }
                    tag.ExcerptRefId = exact.Id;
                }
                NoteAudit.Add(db, noteId, providerId, "theme_excerpt_located", "theme", null, item.Code, now);
                located++;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            result = new ThemeLocateResult(ThemeLocateOutcome.Ok, located, current.Count - located);
        });
        return result;
    }

    private sealed record PendingTag(Guid TagId, string Code);

    // Active tags of the note whose reference is missing or not exact.
    private static async Task<List<PendingTag>> PendingAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, Guid? tagId, CancellationToken ct) =>
        await (
            from tag in db.ThemeTags.AsNoTracking()
            join term in db.ThemeTerms.AsNoTracking() on tag.TermId equals term.Id
            where tag.NoteId == noteId && tag.RemovedAt == null && (tagId == null || tag.Id == tagId)
                  && !db.ExcerptRefs.Any(r => r.Id == tag.ExcerptRefId && r.ProviderId == providerId && r.Kind == "exact")
            orderby tag.CreatedAt
            select new PendingTag(tag.Id, term.Code))
            .ToListAsync(ct);

    internal static async Task<bool?> PatientArchivedAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct)
    {
        var row = await (
            from n in db.Notes.AsNoTracking()
            join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
            where n.Id == noteId && n.ProviderId == providerId && p.ProviderId == providerId
            select new { p.ArchivedAt })
            .SingleOrDefaultAsync(ct);
        return row is null ? null : row.ArchivedAt is not null;
    }

    // Commits only on Ok: refusals must leave no trace.
    private static async Task<ThemeResult> InTransactionAsync(
        AnamnysDbContext db, Func<Task<ThemeResult>> body, CancellationToken ct)
    {
        var result = new ThemeResult(ThemeOutcome.NotFound);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            result = await body();
            if (result.Outcome == ThemeOutcome.Ok)
            {
                await tx.CommitAsync(ct);
            }
        });
        return result;
    }
}
