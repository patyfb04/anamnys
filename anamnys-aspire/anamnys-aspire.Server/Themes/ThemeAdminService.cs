using System.Text.RegularExpressions;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Themes;

public enum ThemeAdminOutcome { Ok, NotFound, Conflict, Invalid }

public sealed record ThemeAdminResult(
    ThemeAdminOutcome Outcome,
    IReadOnlyList<Guid>? Ids = null,
    string? Code = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record NewTermInput(string? Code, string? Label);

public sealed record MergeTargetInput(Guid? ExistingId, string? Code, string? Label);

public sealed record AdminThemeTerm(
    Guid Id, string Code, string Label, Guid? ParentId, string Status, DateOnly IntroducedOn,
    IReadOnlyList<Guid> SuccessorIds, int ActiveTagCount);

// D-01: identity (Id, Code) is fixed and history is never rewritten; tags are never moved or deleted.
// Every mutation runs in one transaction that holds the active vocabulary row, so concurrent edits queue up.
public static partial class ThemeAdminService
{
    private const int MaxLabelLength = 80;

    [GeneratedRegex("^[a-z][a-z0-9_]{1,59}$")]
    private static partial Regex CodePattern();

    public static async Task<IReadOnlyList<AdminThemeTerm>> ListAsync(AnamnysDbContext db, CancellationToken ct)
    {
        var terms = await ThemeVocabularyQuery.ActiveVocabularyAsync(db, ct);
        var ids = terms.Select(t => t.Id).ToList();
        var introduced = await db.ThemeTerms.AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.IntroducedOn, ct);
        var counts = await db.ThemeTags.AsNoTracking()
            .Where(t => t.RemovedAt == null && ids.Contains(t.TermId))
            .GroupBy(t => t.TermId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return terms
            .Select(t => new AdminThemeTerm(
                t.Id, t.Code, t.Label, t.ParentId, t.Status, introduced[t.Id], t.SuccessorIds, counts.GetValueOrDefault(t.Id)))
            .ToList();
    }

    public static Task<ThemeAdminResult> CreateTermAsync(
        AnamnysDbContext db, Guid staffId, NewTermInput input, Guid? parentId, DateTimeOffset now, CancellationToken ct) =>
        RunAsync(db, async vocabularyId =>
        {
            var errors = new Dictionary<string, string[]>();
            var term = Validate(input, "", errors);
            ThemeTerm? parent = null;
            if (parentId is { } pid)
            {
                parent = await db.ThemeTerms.SingleOrDefaultAsync(t => t.Id == pid && t.VocabularyId == vocabularyId, ct);
                if (parent is null || parent.Status != "active" || parent.ParentTermId is not null)
                {
                    errors["parentId"] = ["O tema pai deve ser um tema ativo de primeiro nível."];
                }
            }
            if (errors.Count > 0)
            {
                return Invalid(errors);
            }
            if (await CodeTakenAsync(db, vocabularyId, [term.Code], ct))
            {
                return Conflict("code_taken");
            }

            var created = NewTerm(vocabularyId, term, parent?.Id, now);
            db.ThemeTerms.Add(created);
            db.ThemeTermChanges.Add(Change(created.Id, "add", null, created.Label, staffId, now));
            await db.SaveChangesAsync(ct);
            return Ok(created.Id);
        }, ct);

    public static Task<ThemeAdminResult> RenameAsync(
        AnamnysDbContext db, Guid staffId, Guid termId, string? label, DateTimeOffset now, CancellationToken ct) =>
        RunAsync(db, async vocabularyId =>
        {
            var errors = new Dictionary<string, string[]>();
            var cleaned = ValidLabel(label, "label", errors);
            if (errors.Count > 0)
            {
                return Invalid(errors);
            }
            var term = await db.ThemeTerms.SingleOrDefaultAsync(t => t.Id == termId && t.VocabularyId == vocabularyId, ct);
            if (term is null)
            {
                return NotFound();
            }
            if (term.Status != "active")
            {
                return Conflict("term_not_active");
            }

            db.ThemeTermChanges.Add(Change(term.Id, "rename", term.Label, cleaned, staffId, now));
            term.Label = cleaned;
            await db.SaveChangesAsync(ct);
            return Ok(term.Id);
        }, ct);

    public static Task<ThemeAdminResult> RetireAsync(
        AnamnysDbContext db, Guid staffId, Guid termId, DateTimeOffset now, CancellationToken ct) =>
        RunAsync(db, async vocabularyId =>
        {
            var term = await db.ThemeTerms.SingleOrDefaultAsync(t => t.Id == termId && t.VocabularyId == vocabularyId, ct);
            if (term is null)
            {
                return NotFound();
            }
            if (term.Status != "active")
            {
                return Conflict("term_not_active");
            }
            if (await db.ThemeTerms.AnyAsync(t => t.ParentTermId == termId && t.Status == "active", ct))
            {
                return Conflict("has_active_children");
            }

            term.Status = "retired";
            db.ThemeTermChanges.Add(Change(term.Id, "retire", term.Label, null, staffId, now));
            await db.SaveChangesAsync(ct);
            return Ok(term.Id);
        }, ct);

    public static Task<ThemeAdminResult> SplitAsync(
        AnamnysDbContext db, Guid staffId, Guid termId, IReadOnlyList<NewTermInput>? inputs, DateTimeOffset now, CancellationToken ct) =>
        RunAsync(db, async vocabularyId =>
        {
            var errors = new Dictionary<string, string[]>();
            var news = new List<(string Code, string Label)>();
            if (inputs is null || inputs.Count is < 2 or > 10)
            {
                errors["terms"] = ["Informe de 2 a 10 temas."];
            }
            else
            {
                for (var i = 0; i < inputs.Count; i++)
                {
                    news.Add(Validate(inputs[i], $"terms[{i}].", errors));
                }
                if (news.Select(n => n.Code).Distinct(StringComparer.Ordinal).Count() != news.Count)
                {
                    errors["terms"] = ["Os códigos devem ser diferentes entre si."];
                }
            }
            if (errors.Count > 0)
            {
                return Invalid(errors);
            }
            var old = await db.ThemeTerms.SingleOrDefaultAsync(t => t.Id == termId && t.VocabularyId == vocabularyId, ct);
            if (old is null)
            {
                return NotFound();
            }
            if (old.Status != "active")
            {
                return Conflict("term_not_active");
            }
            if (await db.ThemeTerms.AnyAsync(t => t.ParentTermId == termId && t.Status == "active", ct))
            {
                return Conflict("has_active_children");
            }
            if (await CodeTakenAsync(db, vocabularyId, news.Select(n => n.Code), ct))
            {
                return Conflict("code_taken");
            }

            var created = news.Select(n => NewTerm(vocabularyId, n, old.ParentTermId, now)).ToList();
            db.ThemeTerms.AddRange(created);
            foreach (var term in created)
            {
                db.ThemeTermChanges.Add(Change(term.Id, "add", null, term.Label, staffId, now));
                db.ThemeTermSuccessions.Add(Succession(old.Id, term.Id, "split", staffId, now));
            }
            db.ThemeTermChanges.Add(Change(old.Id, "split", old.Label, string.Join(" | ", created.Select(t => t.Label)), staffId, now));
            old.Status = "superseded";
            await db.SaveChangesAsync(ct);
            return new ThemeAdminResult(ThemeAdminOutcome.Ok, created.Select(t => t.Id).ToList());
        }, ct);

    public static Task<ThemeAdminResult> MergeAsync(
        AnamnysDbContext db, Guid staffId, IReadOnlyList<Guid>? sourceIds, MergeTargetInput? target,
        DateTimeOffset now, CancellationToken ct) =>
        RunAsync(db, async vocabularyId =>
        {
            var errors = new Dictionary<string, string[]>();
            if (sourceIds is null || sourceIds.Count is < 2 or > 10 || sourceIds.Distinct().Count() != sourceIds.Count)
            {
                errors["sourceIds"] = ["Informe de 2 a 10 temas diferentes."];
            }
            (string Code, string Label) newTarget = default;
            var targetIsNew = target is { ExistingId: null };
            if (target is null || (target.ExistingId is null && target.Code is null && target.Label is null)
                || (target.ExistingId is not null && (target.Code is not null || target.Label is not null)))
            {
                errors["target"] = ["Informe um tema existente ou um novo tema (código e rótulo)."];
            }
            else if (targetIsNew)
            {
                newTarget = Validate(new NewTermInput(target.Code, target.Label), "target.", errors);
            }
            if (errors.Count > 0)
            {
                return Invalid(errors);
            }

            var sources = await db.ThemeTerms
                .Where(t => sourceIds!.Contains(t.Id) && t.VocabularyId == vocabularyId)
                .ToListAsync(ct);
            if (sources.Count != sourceIds!.Count)
            {
                return NotFound();
            }
            if (sources.Any(s => s.Status != "active"))
            {
                return Conflict("term_not_active");
            }
            if (await db.ThemeTerms.AnyAsync(t => t.ParentTermId != null && sourceIds.Contains(t.ParentTermId.Value) && t.Status == "active", ct))
            {
                return Conflict("has_active_children");
            }
            var parentId = sources[0].ParentTermId;
            if (sources.Any(s => s.ParentTermId != parentId))
            {
                return Conflict("level_mismatch");
            }

            ThemeTerm merged;
            if (targetIsNew)
            {
                if (await CodeTakenAsync(db, vocabularyId, [newTarget.Code], ct))
                {
                    return Conflict("code_taken");
                }
                merged = NewTerm(vocabularyId, newTarget, parentId, now);
                db.ThemeTerms.Add(merged);
                db.ThemeTermChanges.Add(Change(merged.Id, "add", null, merged.Label, staffId, now));
            }
            else
            {
                if (sourceIds.Contains(target!.ExistingId!.Value))
                {
                    return Invalid(new Dictionary<string, string[]> { ["target"] = ["O destino não pode ser um dos temas de origem."] });
                }
                var existing = await db.ThemeTerms.SingleOrDefaultAsync(
                    t => t.Id == target.ExistingId && t.VocabularyId == vocabularyId, ct);
                if (existing is null)
                {
                    return NotFound();
                }
                if (existing.Status != "active")
                {
                    return Conflict("term_not_active");
                }
                if (existing.ParentTermId != parentId)
                {
                    return Conflict("level_mismatch");
                }
                merged = existing;
            }

            foreach (var source in sources)
            {
                db.ThemeTermChanges.Add(Change(source.Id, "merge", source.Label, merged.Label, staffId, now));
                db.ThemeTermSuccessions.Add(Succession(source.Id, merged.Id, "merge", staffId, now));
                source.Status = "superseded";
                source.SupersededByTermId = merged.Id;
            }
            await db.SaveChangesAsync(ct);
            return Ok(merged.Id);
        }, ct);

    // Commits only on Ok: refusals must leave no trace.
    private static async Task<ThemeAdminResult> RunAsync(
        AnamnysDbContext db, Func<Guid, Task<ThemeAdminResult>> body, CancellationToken ct)
    {
        var result = NotFound();
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var locked = await db.Database
                .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "ThemeVocabularies" WHERE "Active" FOR UPDATE""")
                .ToListAsync(ct);
            if (locked.Count == 0)
            {
                result = NotFound();
                return;
            }
            result = await body(locked[0]);
            if (result.Outcome == ThemeAdminOutcome.Ok)
            {
                await tx.CommitAsync(ct);
            }
        });
        return result;
    }

    private static Task<bool> CodeTakenAsync(AnamnysDbContext db, Guid vocabularyId, IEnumerable<string> codes, CancellationToken ct)
    {
        var list = codes.ToList();
        return db.ThemeTerms.AnyAsync(t => t.VocabularyId == vocabularyId && list.Contains(t.Code), ct);
    }

    private static (string Code, string Label) Validate(NewTermInput input, string prefix, Dictionary<string, string[]> errors)
    {
        var code = input.Code ?? "";
        if (!CodePattern().IsMatch(code))
        {
            errors[$"{prefix}code"] = ["Código inválido: use letras minúsculas, números e _, de 2 a 60 caracteres, começando por letra."];
        }
        return (code, ValidLabel(input.Label, $"{prefix}label", errors));
    }

    private static string ValidLabel(string? label, string key, Dictionary<string, string[]> errors)
    {
        var cleaned = label?.Trim() ?? "";
        if (cleaned.Length is 0 or > MaxLabelLength)
        {
            errors[key] = [$"O rótulo deve ter de 1 a {MaxLabelLength} caracteres."];
        }
        return cleaned;
    }

    private static ThemeTerm NewTerm(Guid vocabularyId, (string Code, string Label) term, Guid? parentId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        VocabularyId = vocabularyId,
        Code = term.Code,
        Label = term.Label,
        ParentTermId = parentId,
        IntroducedOn = DateOnly.FromDateTime(now.UtcDateTime),
        Status = "active",
    };

    private static ThemeTermChange Change(Guid termId, string kind, string? previous, string? next, Guid staffId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TermId = termId,
        Kind = kind,
        PreviousLabel = previous,
        NewLabel = next,
        StaffId = staffId,
        At = now,
    };

    private static ThemeTermSuccession Succession(Guid from, Guid to, string kind, Guid staffId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        FromTermId = from,
        ToTermId = to,
        Kind = kind,
        StaffId = staffId,
        At = now,
    };

    private static ThemeAdminResult Ok(Guid id) => new(ThemeAdminOutcome.Ok, [id]);

    private static ThemeAdminResult NotFound() => new(ThemeAdminOutcome.NotFound);

    private static ThemeAdminResult Conflict(string code) => new(ThemeAdminOutcome.Conflict, Code: code);

    private static ThemeAdminResult Invalid(IReadOnlyDictionary<string, string[]> errors) => new(ThemeAdminOutcome.Invalid, Errors: errors);
}
