namespace Anamnys.Server.Analysis;

public sealed record AbsenceSession(Guid NoteId, DateOnly On);

public sealed record AbsenceRecord(Guid NoteId, Guid? ExcerptId, string? Section, string? Quote);

// Records are the term's own tags in session order. Terms without records are listed only so that the parent
// links can be followed.
public sealed record AbsenceTerm(Guid TermId, string Code, string Label, Guid? ParentId, bool Active, IReadOnlyList<AbsenceRecord> Records);

public sealed record AbsenceLastOccurrence(Guid NoteId, Guid? ExcerptId, string? Section, string? Quote);

public sealed record ThemeAbsenceItem(
    Guid TermId, string Code, string Label, Guid? ParentId, int RecordedIn, int SpanSessions,
    DateOnly FirstOn, DateOnly LastOn, int SessionsSince, int DaysSince, AbsenceLastOccurrence LastOccurrence);

// Themes recorded repeatedly that then stopped (design/specs/2026-10-29-theme-absence-design.md §1, §3). Pure.
// A term is absent when it was recorded in at least MinimumRecordedSessions sessions and in none of the last N.
// Superseded and retired terms are never flagged; a parent is flagged only when neither it nor any descendant
// was recorded in the last N; a child is flagged on its own rule. Fewer than the case analysis minimum of
// sessions: none.
public static class ThemeAbsence
{
    public const int MinimumRecordedSessions = 3;
    public const int DefaultSessions = 4;
    public const int MinSessions = 2;
    public const int MaxSessions = 20;

    public static IReadOnlyList<ThemeAbsenceItem> Detect(
        IReadOnlyList<AbsenceSession> sessions, IReadOnlyList<AbsenceTerm> terms, int n, DateOnly today)
    {
        if (sessions.Count < CaseAnalysisQuery.MinimumSessions || sessions.Count <= n)
        {
            return [];
        }

        var indexOf = new Dictionary<Guid, int>();
        for (var i = 0; i < sessions.Count; i++)
        {
            indexOf[sessions[i].NoteId] = i;
        }
        var windowStart = sessions.Count - n;
        var byId = terms.ToDictionary(t => t.TermId);

        // Every term recorded inside the window, and every ancestor of one, is alive.
        var alive = new HashSet<Guid>();
        foreach (var term in terms)
        {
            if (!term.Records.Any(r => indexOf.TryGetValue(r.NoteId, out var at) && at >= windowStart))
            {
                continue;
            }
            Guid? id = term.TermId;
            while (id is { } current && alive.Add(current))
            {
                id = byId.GetValueOrDefault(current)?.ParentId;
            }
        }

        var items = new List<ThemeAbsenceItem>();
        foreach (var term in terms)
        {
            if (!term.Active || alive.Contains(term.TermId))
            {
                continue;
            }
            var records = term.Records.Where(r => indexOf.ContainsKey(r.NoteId)).OrderBy(r => indexOf[r.NoteId]).ToList();
            var recordedSessions = records.Select(r => indexOf[r.NoteId]).Distinct().ToList();
            if (recordedSessions.Count < MinimumRecordedSessions)
            {
                continue;
            }
            var first = recordedSessions[0];
            var last = recordedSessions[^1];
            var inLast = records.Where(r => indexOf[r.NoteId] == last).ToList();
            var passage = inLast.LastOrDefault(r => r.ExcerptId != null) ?? inLast[^1];
            items.Add(new ThemeAbsenceItem(
                term.TermId, term.Code, term.Label, term.ParentId, recordedSessions.Count, last - first + 1,
                sessions[first].On, sessions[last].On, sessions.Count - 1 - last, Math.Max(0, today.DayNumber - sessions[last].On.DayNumber),
                new AbsenceLastOccurrence(passage.NoteId, passage.ExcerptId, passage.Section, passage.Quote)));
        }
        return items.OrderBy(i => i.LastOn).ThenBy(i => i.Label, StringComparer.CurrentCulture).ThenBy(i => i.TermId).ToList();
    }
}
