using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Analysis;

public sealed record PlanAdherencePlan(Guid? PlanId, DateTimeOffset? ReviewedAt);

public sealed record PlanAdherenceTerm(Guid TermId, string Label);

// FirstOn/LastOn/LastNoteId: the first and last signed session in which the term or one of its descendants was recorded
// (whole history, not only after the definition). SessionsSince/DaysSince: signed sessions after the last one and days
// from its day to today; when the term was never recorded, counted from the definition (sessions after CreatedAt, days
// from its practice day). All five are null without a term.
public sealed record PlanAdherenceObjective(
    Guid ObjectiveId, string Description, DateTimeOffset CreatedAt, PlanAdherenceTerm? Term,
    DateOnly? FirstOn, DateOnly? LastOn, Guid? LastNoteId, int? SessionsSince, int? DaysSince);

public sealed record PlanAdherenceExcerpt(Guid ExcerptId, string? Section, string? Quote);

// SessionsSince: signed sessions after the note it was marked on (excluding that note); for a resolved one, only those up
// to the resolution (the resolving note included when signed). OriginOn: the origin note's day by the F-10 session date
// rule (practice-local), null when that note is gone.
public sealed record PlanAdherenceFollowUp(
    Guid Id, string Description, DateTimeOffset CreatedAt, Guid? NoteId, PlanAdherenceExcerpt? Excerpt,
    DateTimeOffset? ResolvedAt, Guid? ResolvedNoteId, int SessionsSince, DateOnly? OriginOn = null);

public sealed record PlanAdherence(
    PlanAdherencePlan Plan, IReadOnlyList<PlanAdherenceObjective> Objectives, IReadOnlyList<PlanAdherenceFollowUp> FollowUps);

// The "Plano terapêutico" card (design/specs/2026-10-30-plan-adherence-design.md §3): the open plan's review date, its
// objectives compared with the themes recorded in signed sessions (same rules and session date as the case analysis,
// CaseAnalysisQuery), and the patient's follow-ups, open ones first (oldest first), then resolved ones (latest
// resolution first). Observation only. Ids, dates, labels and the stored quote; fixed number of queries.
public static class PlanAdherenceQuery
{
    public static async Task<PlanAdherence?> ExecuteAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct, DateOnly? today = null)
    {
        if (!await db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null, ct))
        {
            return null;
        }
        var day = today ?? PatientSearchQuery.PracticeToday(DateTimeOffset.UtcNow);

        var plan = await db.TreatmentPlans.AsNoTracking()
            .Where(t => t.PatientId == patientId && t.ClosedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.ReviewedAt })
            .FirstOrDefaultAsync(ct);
        var objectives = plan is null
            ? []
            : await (
                from o in db.PlanObjectives.AsNoTracking()
                join t in db.ThemeTerms.AsNoTracking() on o.TermId equals (Guid?)t.Id into terms
                from t in terms.DefaultIfEmpty()
                where o.PlanId == plan.Id
                orderby o.CreatedAt, o.Id
                select new { o.Id, o.Description, o.CreatedAt, o.TermId, Label = t == null ? null : t.Label })
                .ToListAsync(ct);

        var notes = await CaseAnalysisQuery.DatedNotesAsync(db, providerId, patientId, signedOnly: false, ct);
        var history = notes.Where(n => n.Signed).ToList();
        var position = history.Select((n, i) => (n.NoteId, i)).ToDictionary(x => x.NoteId, x => x.i);

        // Each linked term with its descendants (any depth), then the counted tags of the signed history on any of them.
        var linked = objectives.Where(o => o.TermId != null).Select(o => o.TermId!.Value).Distinct().ToList();
        var descendants = linked.ToDictionary(id => id, id => new HashSet<Guid> { id });
        if (linked.Count > 0)
        {
            var parentOf = new Dictionary<Guid, Guid>();
            var frontier = linked;
            while (frontier.Count > 0)
            {
                var children = await db.ThemeTerms.AsNoTracking()
                    .Where(t => t.ParentTermId != null && frontier.Contains(t.ParentTermId.Value))
                    .Select(t => new { t.Id, Parent = t.ParentTermId!.Value })
                    .ToListAsync(ct);
                frontier = [];
                foreach (var child in children.Where(c => parentOf.TryAdd(c.Id, c.Parent)))
                {
                    frontier.Add(child.Id);
                }
            }
            foreach (var (child, _) in parentOf)
            {
                for (var up = child; parentOf.TryGetValue(up, out var parent); up = parent)
                {
                    if (descendants.TryGetValue(parent, out var set))
                    {
                        set.Add(child);
                    }
                }
            }
        }
        var allTerms = descendants.Values.SelectMany(s => s).Distinct().ToList();
        var historyIds = position.Keys.ToList();
        var tags = allTerms.Count == 0
            ? []
            : await CaseAnalysisQuery.CountedTags(db)
                .Where(t => allTerms.Contains(t.TermId) && historyIds.Contains(t.NoteId))
                .Select(t => new { t.TermId, t.NoteId })
                .ToListAsync(ct);

        var objectiveRows = objectives.Select(o =>
        {
            if (o.TermId is not { } termId)
            {
                return new PlanAdherenceObjective(o.Id, o.Description, o.CreatedAt, null, null, null, null, null, null);
            }
            var set = descendants[termId];
            var recorded = tags.Where(t => set.Contains(t.TermId)).Select(t => position[t.NoteId]).Distinct().Order().ToList();
            var term = new PlanAdherenceTerm(termId, o.Label ?? "");
            if (recorded.Count == 0)
            {
                var since = history.Count(n => n.At > o.CreatedAt);
                return new PlanAdherenceObjective(o.Id, o.Description, o.CreatedAt, term, null, null, null,
                    since, day.DayNumber - PatientSearchQuery.PracticeToday(o.CreatedAt).DayNumber);
            }
            var first = history[recorded[0]];
            var last = history[recorded[^1]];
            return new PlanAdherenceObjective(o.Id, o.Description, o.CreatedAt, term, first.On, last.On, last.NoteId,
                history.Count - 1 - recorded[^1], day.DayNumber - last.On.DayNumber);
        }).ToList();

        var items = await (
            from f in db.FollowUpItems.AsNoTracking()
            join r in db.ExcerptRefs.AsNoTracking().Where(r => r.ProviderId == providerId) on f.ExcerptRefId equals (Guid?)r.Id into refs
            from r in refs.DefaultIfEmpty()
            where f.ProviderId == providerId && f.PatientId == patientId
            select new
            {
                f.Id, f.Description, f.CreatedAt, f.NoteId, f.ResolvedAt, f.ResolvedNoteId,
                RefId = (Guid?)r.Id, r.SectionKey, r.Quote,
            })
            .ToListAsync(ct);
        var dated = notes.ToDictionary(n => n.NoteId);
        var followUps = items
            .Select(f =>
            {
                var originNote = f.NoteId is { } origin ? dated.GetValueOrDefault(origin) : null;
                var from = originNote?.At ?? f.CreatedAt;
                DateTimeOffset? until = f.ResolvedAt is null ? null
                    : f.ResolvedNoteId is { } resolving && dated.TryGetValue(resolving, out var resolvingNote) ? resolvingNote.At
                    : f.ResolvedAt;
                var since = history.Count(n => n.NoteId != f.NoteId && n.At > from && (until == null || n.At <= until));
                return new PlanAdherenceFollowUp(
                    f.Id, f.Description, f.CreatedAt, f.NoteId,
                    f.RefId is { } refId ? new PlanAdherenceExcerpt(refId, f.SectionKey, f.Quote) : null,
                    f.ResolvedAt, f.ResolvedNoteId, since, originNote?.On);
            })
            .OrderBy(f => f.ResolvedAt != null)
            .ThenBy(f => f.ResolvedAt == null ? f.CreatedAt.UtcTicks : -f.ResolvedAt.Value.UtcTicks)
            .ThenBy(f => f.Id)
            .ToList();

        return new PlanAdherence(new PlanAdherencePlan(plan?.Id, plan?.ReviewedAt), objectiveRows, followUps);
    }
}
