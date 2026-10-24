using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Themes;

public sealed record ThemeVocabularyTerm(
    Guid Id, string Code, string Label, Guid? ParentId, string Status, IReadOnlyList<Guid> SuccessorIds);

public sealed record ThemeTermOption(Guid Id, string Code, string Label, string? ParentLabel);

public static class ThemeVocabularyQuery
{
    // Active terms of the active vocabulary, each top-level term followed by its children.
    public static async Task<IReadOnlyList<ThemeTermOption>> ActiveTermOptionsAsync(AnamnysDbContext db, CancellationToken ct)
    {
        var terms = await (
            from t in db.ThemeTerms.AsNoTracking()
            join v in db.ThemeVocabularies.AsNoTracking() on t.VocabularyId equals v.Id
            where v.Active
            select new { t.Id, t.Code, t.Label, t.ParentTermId, t.Status })
            .ToListAsync(ct);
        var labels = terms.ToDictionary(t => t.Id, t => t.Label);
        return terms
            .Where(t => t.Status == "active")
            .Select(t => new ThemeTermOption(t.Id, t.Code, t.Label,
                t.ParentTermId is { } parent ? labels.GetValueOrDefault(parent) : null))
            .OrderBy(t => t.ParentLabel ?? t.Label, StringComparer.CurrentCulture)
            .ThenBy(t => t.ParentLabel is null ? 0 : 1)
            .ThenBy(t => t.Label, StringComparer.CurrentCulture)
            .ToList();
    }

    public static async Task<IReadOnlyList<ThemeVocabularyTerm>> ActiveVocabularyAsync(AnamnysDbContext db, CancellationToken ct)
    {
        var terms = await (
            from t in db.ThemeTerms.AsNoTracking()
            join v in db.ThemeVocabularies.AsNoTracking() on t.VocabularyId equals v.Id
            where v.Active
            select new { t.Id, t.Code, t.Label, t.ParentTermId, t.Status, t.SupersededByTermId })
            .ToListAsync(ct);
        var ids = terms.Select(t => t.Id).ToList();
        var successions = await db.ThemeTermSuccessions.AsNoTracking()
            .Where(s => ids.Contains(s.FromTermId))
            .Select(s => new { s.FromTermId, s.ToTermId })
            .ToListAsync(ct);
        var byFrom = successions.GroupBy(s => s.FromTermId).ToDictionary(g => g.Key, g => g.Select(s => s.ToTermId).ToList());

        return terms
            .OrderBy(t => t.Label, StringComparer.CurrentCulture)
            .Select(t =>
            {
                var successors = byFrom.GetValueOrDefault(t.Id) ?? [];
                if (t.SupersededByTermId is { } merged && !successors.Contains(merged))
                {
                    successors = [.. successors, merged];
                }
                return new ThemeVocabularyTerm(t.Id, t.Code, t.Label, t.ParentTermId, t.Status, successors);
            })
            .ToList();
    }
}
