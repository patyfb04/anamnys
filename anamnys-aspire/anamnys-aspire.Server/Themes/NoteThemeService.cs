using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notes;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Themes;

public enum ThemeOutcome { Ok, NotFound, Archived, TermInactive, AlreadyTagged }

public sealed record ThemeResult(ThemeOutcome Outcome, Guid? Id = null);

public enum ThemeSuggestOutcome { Ok, NotFound, Failed }

public sealed record ThemeSuggestion(ThemeSuggestOutcome Outcome, IReadOnlyList<ThemeTermOption> Terms);

public sealed record NoteThemeItem(
    Guid Id, Guid TermId, string Code, string Label, string? ParentLabel, string TermStatus,
    string Origin, bool Proposed, DateTimeOffset AddedAt);

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
            where tag.NoteId == noteId && tag.RemovedAt == null
            select new { tag, term })
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
                r.term.Status, r.tag.CreatedBy, r.tag.CreatedBy == "ai" && r.tag.AddedById is null, r.tag.CreatedAt))
            .ToList();
    }

    public static Task<ThemeResult> AddAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, Guid termId, bool fromSuggestion,
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

            var tag = new ThemeTag
            {
                Id = Guid.NewGuid(),
                NoteId = noteId,
                TermId = termId,
                CreatedBy = fromSuggestion ? "ai" : "human",
                CreatedAt = now,
                AddedById = providerId,
            };
            db.ThemeTags.Add(tag);
            NoteAudit.Add(db, noteId, providerId, "theme_added", "theme", null, term.Code, now);
            await db.SaveChangesAsync(ct);
            return new ThemeResult(ThemeOutcome.Ok, tag.Id);
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
    // produced by the model (grammar) nor returned. A model failure is logged without the text.
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
        // are excluded through the grammar.
        var all = await ThemeVocabularyQuery.ActiveTermOptionsAsync(db, ct);
        var options = all.Where(o => !tagged.Contains(o.Id)).ToList();
        if (options.Count == 0)
        {
            return new ThemeSuggestion(ThemeSuggestOutcome.Ok, []);
        }
        var excluded = all.Where(o => tagged.Contains(o.Id)).Select(o => o.Code).ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<string> codes;
        try
        {
            codes = await suggester.SuggestAsync(note.Text, all, excluded, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError("Theme suggestion failed for note {NoteId}: {ExceptionType}", noteId, e.GetType().Name);
            return new ThemeSuggestion(ThemeSuggestOutcome.Failed, []);
        }
        var byCode = options.ToDictionary(o => o.Code, StringComparer.Ordinal);
        return new ThemeSuggestion(ThemeSuggestOutcome.Ok,
            codes.Where(byCode.ContainsKey).Distinct(StringComparer.Ordinal).Take(ThemeGrammar.MaxItems).Select(c => byCode[c]).ToList());
    }

    private static async Task<bool?> PatientArchivedAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct)
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
