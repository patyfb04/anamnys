using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Analysis;

public sealed record CaseAnalysisSession(Guid NoteId, DateOnly On, Guid? SessionId, Guid? AppointmentId);

public sealed record CaseAnalysisSuperseded(Guid TermId, string Label);

public sealed record CaseAnalysisOccurrence(Guid NoteId, DateOnly On, Guid? ExcerptId, string? Section, string? Quote);

public sealed record CaseAnalysisTheme(
    Guid TermId, string Code, string Label, Guid? ParentId, bool Active,
    IReadOnlyList<CaseAnalysisSuperseded> SupersededBy, IReadOnlyList<CaseAnalysisOccurrence> Occurrences);

public sealed record CaseAnalysisPair(Guid A, Guid B, int Sessions);

// When Available is false only the first three fields are filled.
public sealed record CaseAnalysis(
    bool Available, int SignedSessions, int MinimumSessions,
    IReadOnlyList<CaseAnalysisSession>? Sessions, IReadOnlyList<CaseAnalysisTheme>? Themes, IReadOnlyList<CaseAnalysisPair>? Cooccurrence);

// One patient's longitudinal theme history (design/specs/2026-10-28-case-analysis-design.md §2, §3).
// Signed notes are the session columns; a note's day is its session's actual start, else its appointment's start,
// else the signing day (practice-local). Tags count when not removed and not an untouched AI proposal
// (CreatedBy 'ai' with no AddedById). Fixed number of queries; ids, dates, labels and the stored quote only.
public static class CaseAnalysisQuery
{
    public const int MinimumSessions = 6;
    private static readonly string[] Signed = ["Signed", "Exported"];

    public static async Task<CaseAnalysis?> ExecuteAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!await db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null, ct))
        {
            return null;
        }

        var notes = await db.Notes.AsNoTracking()
            .Where(n => n.ProviderId == providerId && n.PatientId == patientId && Signed.Contains(n.Status))
            .Select(n => new { n.Id, n.SessionId, n.SignedAt, n.CreatedAt })
            .ToListAsync(ct);
        var sessionIds = notes.Where(n => n.SessionId != null).Select(n => n.SessionId!.Value).Distinct().ToList();
        var actualStarts = await db.Sessions.AsNoTracking()
            .Where(s => sessionIds.Contains(s.Id) && s.ActualStart != null)
            .Select(s => new { s.Id, Start = s.ActualStart!.Value })
            .ToDictionaryAsync(s => s.Id, s => s.Start, ct);
        var appointmentRows = await db.Appointments.AsNoTracking()
            .Where(a => a.ProviderId == providerId && a.PatientId == patientId && a.SessionId != null && sessionIds.Contains(a.SessionId.Value))
            .Select(a => new { a.Id, SessionId = a.SessionId!.Value, a.StartsAt })
            .ToListAsync(ct);
        var appointments = appointmentRows
            .GroupBy(a => a.SessionId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.StartsAt).ThenBy(a => a.Id).First());

        var sessions = notes
            .Select(n =>
            {
                appointments.TryGetValue(n.SessionId ?? Guid.Empty, out var appointment);
                var at = n.SessionId is { } sid && actualStarts.TryGetValue(sid, out var start) ? start
                    : appointment?.StartsAt ?? n.SignedAt ?? n.CreatedAt;
                return new { Row = new CaseAnalysisSession(n.Id, PatientSearchQuery.PracticeToday(at), n.SessionId, appointment?.Id), At = at };
            })
            .Where(s => (from == null || s.Row.On >= from) && (to == null || s.Row.On <= to))
            .OrderBy(s => s.Row.On).ThenBy(s => s.At).ThenBy(s => s.Row.NoteId)
            .Select(s => s.Row)
            .ToList();
        if (sessions.Count < MinimumSessions)
        {
            return new CaseAnalysis(false, sessions.Count, MinimumSessions, null, null, null);
        }

        var dayOf = sessions.ToDictionary(s => s.NoteId, s => s.On);
        var noteIds = dayOf.Keys.ToList();
        var tags = await (
            from tag in db.ThemeTags.AsNoTracking()
            join term in db.ThemeTerms.AsNoTracking() on tag.TermId equals term.Id
            join r in db.ExcerptRefs.AsNoTracking().Where(r => r.ProviderId == providerId && r.SourceType == "note") on new { Id = tag.ExcerptRefId, Note = tag.NoteId } equals new { Id = (Guid?)r.Id, Note = r.SourceId } into refs
            from r in refs.DefaultIfEmpty()
            where noteIds.Contains(tag.NoteId) && tag.RemovedAt == null && !(tag.CreatedBy == "ai" && tag.AddedById == null)
            select new
            {
                tag.NoteId, tag.CreatedAt, term.Id, term.Code, term.Label, term.ParentTermId, term.Status,
                RefId = (Guid?)r.Id, r.SectionKey, r.Quote,
            })
            .ToListAsync(ct);

        // Every listed child brings its parent (and its ancestors) even without occurrences, so the UI can nest.
        var termIds = tags.Select(t => t.Id).Distinct().ToList();
        var parents = new List<ThemeTerm>();
        var missing = tags.Where(t => t.ParentTermId != null).Select(t => t.ParentTermId!.Value).Distinct().Except(termIds).ToList();
        while (missing.Count > 0)
        {
            var loaded = await db.ThemeTerms.AsNoTracking().Where(t => missing.Contains(t.Id)).ToListAsync(ct);
            parents.AddRange(loaded);
            var known = termIds.Concat(parents.Select(p => p.Id)).ToHashSet();
            missing = loaded.Where(t => t.ParentTermId != null).Select(t => t.ParentTermId!.Value).Distinct().Where(id => !known.Contains(id)).ToList();
        }
        termIds.AddRange(parents.Select(p => p.Id));
        var successions = await (
            from s in db.ThemeTermSuccessions.AsNoTracking()
            join target in db.ThemeTerms.AsNoTracking() on s.ToTermId equals target.Id
            where termIds.Contains(s.FromTermId)
            select new { s.FromTermId, To = target.Id, target.Label, s.At })
            .ToListAsync(ct);
        var supersededBy = successions
            .GroupBy(s => s.FromTermId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CaseAnalysisSuperseded>)g.OrderBy(s => s.At).ThenBy(s => s.Label, StringComparer.CurrentCulture)
                    .Select(s => new CaseAnalysisSuperseded(s.To, s.Label)).ToList());

        var themes = tags
            .GroupBy(t => t.Id)
            .Select(g =>
            {
                var first = g.First();
                return new CaseAnalysisTheme(
                    first.Id, first.Code, first.Label, first.ParentTermId, first.Status == "active",
                    supersededBy.GetValueOrDefault(first.Id) ?? [],
                    g.OrderBy(t => dayOf[t.NoteId]).ThenBy(t => t.CreatedAt).ThenBy(t => t.NoteId)
                        .Select(t => new CaseAnalysisOccurrence(t.NoteId, dayOf[t.NoteId], t.RefId, t.SectionKey, t.Quote)).ToList());
            })
            .Concat(parents.Select(p => new CaseAnalysisTheme(
                p.Id, p.Code, p.Label, p.ParentTermId, p.Status == "active", supersededBy.GetValueOrDefault(p.Id) ?? [], [])))
            .OrderBy(t => t.Code, StringComparer.Ordinal)
            .ToList();

        var labels = tags.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Label);
        var pairs = new Dictionary<(Guid, Guid), int>();
        foreach (var perNote in tags.GroupBy(t => t.NoteId))
        {
            var ids = perNote.Select(t => t.Id).Distinct()
                .OrderBy(id => labels[id], StringComparer.CurrentCulture).ThenBy(id => id).ToList();
            for (var i = 0; i < ids.Count; i++)
            {
                for (var j = i + 1; j < ids.Count; j++)
                {
                    pairs[(ids[i], ids[j])] = pairs.GetValueOrDefault((ids[i], ids[j])) + 1;
                }
            }
        }
        var cooccurrence = pairs
            .Select(p => new CaseAnalysisPair(p.Key.Item1, p.Key.Item2, p.Value))
            .OrderByDescending(p => p.Sessions)
            .ThenBy(p => labels[p.A], StringComparer.CurrentCulture).ThenBy(p => labels[p.B], StringComparer.CurrentCulture)
            .ThenBy(p => p.A).ThenBy(p => p.B)
            .ToList();

        return new CaseAnalysis(true, sessions.Count, MinimumSessions, sessions, themes, cooccurrence);
    }
}
