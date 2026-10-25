using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Excerpts;
using Anamnys.Server.Notes;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Themes;

// AI theme proposals written after structuring (design/specs/2026-10-21-theme-vocabulary-design.md §4).
// Only untouched proposals (CreatedBy 'ai', AddedById null) are replaced; tags the provider added or
// accepted are never touched, and nothing is proposed on a signed note. Never throws: a failure is
// logged without the text and leaves structuring as it was.
public static class ThemeProposals
{
    private static readonly string[] Signed = ["Signed", "Exported"];

    internal sealed record NoteForThemes(string Status, bool Archived, string Text);

    // Provider-scoped through the note and the patient; null when the note is not the provider's.
    internal static async Task<NoteForThemes?> LoadNoteAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct)
    {
        var note = await (
            from n in db.Notes.AsNoTracking()
            join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
            where n.Id == noteId && n.ProviderId == providerId && p.ProviderId == providerId
            select new { n.Status, n.RawTranscript, p.ArchivedAt })
            .SingleOrDefaultAsync(ct);
        if (note is null)
        {
            return null;
        }
        var sections = await db.NoteSections.AsNoTracking()
            .Where(s => s.NoteId == noteId)
            .OrderBy(s => s.Position)
            .Select(s => new { s.Key, s.Content })
            .ToListAsync(ct);
        return new NoteForThemes(note.Status, note.ArchivedAt is not null,
            ThemePrompt.NoteText(sections.Select(s => (s.Key, s.Content)).ToList(), note.RawTranscript));
    }

    public static async Task ProposeAsync(
        AnamnysDbContext db, IThemeSuggester suggester, Guid noteId, Guid providerId,
        TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        try
        {
            var note = await LoadNoteAsync(db, providerId, noteId, ct);
            if (note is null || note.Archived || Signed.Contains(note.Status) || string.IsNullOrWhiteSpace(note.Text))
            {
                return;
            }
            var options = await ThemeVocabularyQuery.ActiveTermOptionsAsync(db, ct);
            if (options.Count == 0)
            {
                return;
            }
            // A term the provider removed from this note is kept out by the grammar (and re-checked
            // under the lock in ReplaceAsync).
            var removed = await db.ThemeTags.AsNoTracking()
                .Where(t => t.NoteId == noteId && t.RemovedById != null)
                .Select(t => t.TermId)
                .ToListAsync(ct);
            var excluded = options.Where(o => removed.Contains(o.Id)).Select(o => o.Code).ToHashSet(StringComparer.Ordinal);
            // Nobody waits on proposals: they come with quotes, so their references are exact when located.
            var items = await suggester.SuggestAsync(note.Text, options, excluded, withQuotes: true, ct);
            var byCode = options.Where(o => !excluded.Contains(o.Code)).ToDictionary(o => o.Code, StringComparer.Ordinal);
            var chosen = items.Where(i => byCode.ContainsKey(i.Code)).DistinctBy(i => i.Code)
                .Take(ThemeGrammar.MaxItems).Select(i => (byCode[i.Code].Id, i.Quote)).ToList();
            await ReplaceAsync(db, noteId, providerId, chosen, clock.GetUtcNow(), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError("Theme proposal failed for note {NoteId}: {ExceptionType}", noteId, e.GetType().Name);
        }
    }

    private static async Task ReplaceAsync(
        AnamnysDbContext db, Guid noteId, Guid providerId, IReadOnlyList<(Guid TermId, string? Quote)> proposed,
        DateTimeOffset now, CancellationToken ct)
    {
        var chosen = proposed.Select(p => p.TermId).ToList();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await NoteLocks.LockAsync(db, noteId, ct);
            // Re-checked under the lock: the note may have been signed, or the patient archived, while the model ran.
            var current = await (
                from n in db.Notes.AsNoTracking()
                join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
                where n.Id == noteId && n.ProviderId == providerId && p.ProviderId == providerId
                select new { n.Status, p.ArchivedAt })
                .SingleOrDefaultAsync(ct);
            if (current is null || current.ArchivedAt is not null || Signed.Contains(current.Status))
            {
                return;
            }
            // A term retired while the model ran is not proposed.
            var stillActive = await (
                from t in db.ThemeTerms.AsNoTracking()
                join v in db.ThemeVocabularies.AsNoTracking() on t.VocabularyId equals v.Id
                where chosen.Contains(t.Id) && v.Active && t.Status == "active"
                select t.Id)
                .ToListAsync(ct);
            // A term the provider removed from this note is never proposed on it again.
            var rejected = await db.ThemeTags.AsNoTracking()
                .Where(t => t.NoteId == noteId && t.RemovedById != null)
                .Select(t => t.TermId)
                .ToListAsync(ct);
            var wanted = chosen.Where(id => stillActive.Contains(id) && !rejected.Contains(id)).ToList();

            var active = await db.ThemeTags.Where(t => t.NoteId == noteId && t.RemovedAt == null).ToListAsync(ct);
            var withdrawn = active.Where(t => t.CreatedBy == "ai" && t.AddedById is null && !wanted.Contains(t.TermId)).ToList();
            var added = wanted.Where(id => active.All(t => t.TermId != id)).ToList();
            if (withdrawn.Count == 0 && added.Count == 0)
            {
                return;
            }
            var termIds = withdrawn.Select(t => t.TermId).Concat(added).ToList();
            var codes = await db.ThemeTerms.AsNoTracking()
                .Where(t => termIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Code, ct);

            // An untouched proposal was never the provider's: it is withdrawn, not removed by anyone
            // (RemovedAt requires RemovedById), and the withdrawal is audited.
            // The reference of a withdrawn proposal goes with it.
            var withdrawnRefs = withdrawn.Where(t => t.ExcerptRefId != null).Select(t => t.ExcerptRefId!.Value).ToList();
            if (withdrawnRefs.Count > 0)
            {
                db.ExcerptRefs.RemoveRange(await db.ExcerptRefs.Where(r => withdrawnRefs.Contains(r.Id)).ToListAsync(ct));
            }
            foreach (var tag in withdrawn)
            {
                db.ThemeTags.Remove(tag);
                NoteAudit.Add(db, noteId, null, "theme_proposal_withdrawn", "theme", codes[tag.TermId], null, now);
            }
            foreach (var termId in added)
            {
                // Exact when the model's quote is found in a section, else the first non-empty section.
                var reference = await ExcerptService.AddAsync(
                    db, providerId, noteId, null, proposed.First(p => p.TermId == termId).Quote, null, null, ct);
                db.ThemeTags.Add(new ThemeTag
                {
                    Id = Guid.NewGuid(),
                    NoteId = noteId,
                    TermId = termId,
                    ExcerptRefId = reference.Ref?.Id,
                    CreatedBy = "ai",
                    CreatedAt = now,
                });
                NoteAudit.Add(db, noteId, null, "theme_proposed", "theme", null, codes[termId], now);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }
}
