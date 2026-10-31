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

// When Available is false only the first three fields, Absences and AbsenceSessions are filled.
// Absences always cover the patient's whole signed history, whatever the period and its availability (F-12):
// they are empty only when the whole history has fewer than MinimumSessions signed sessions.
public sealed record CaseAnalysis(
    bool Available, int SignedSessions, int MinimumSessions,
    IReadOnlyList<CaseAnalysisSession>? Sessions, IReadOnlyList<CaseAnalysisTheme>? Themes, IReadOnlyList<CaseAnalysisPair>? Cooccurrence,
    IReadOnlyList<ThemeAbsenceItem> Absences, int AbsenceSessions);

// One patient's longitudinal theme history (design/specs/2026-10-28-case-analysis-design.md §2, §3).
// Signed notes are the session columns; a note's day is its session's actual start, else its appointment's start,
// else the signing day (practice-local). Tags count when not removed and not an untouched AI proposal
// (CreatedBy 'ai' with no AddedById). Fixed number of queries; ids, dates, labels and the stored quote only.
// Theme absences (design/specs/2026-10-29-theme-absence-design.md) are computed from the same tags over the whole
// signed history, in memory.
public static class CaseAnalysisQuery
{
    public const int MinimumSessions = 6;
    private static readonly string[] Signed = ["Signed", "Exported"];

    private sealed record TermInfo(Guid Id, string Code, string Label, Guid? ParentId, string Status);

    public static async Task<CaseAnalysis?> ExecuteAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateOnly? from, DateOnly? to, CancellationToken ct, DateOnly? today = null)
    {
        if (!await db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null, ct))
        {
            return null;
        }
        var absenceSessions = await AnalysisSettings.AbsenceSessionsAsync(db, providerId, ct);

        // The whole signed history, in order; the period only narrows what the page lists.
        var history = (await DatedNotesAsync(db, providerId, patientId, signedOnly: true, ct))
            .Select(n => new CaseAnalysisSession(n.NoteId, n.On, n.SessionId, n.AppointmentId))
            .ToList();
        var sessions = history.Where(s => (from == null || s.On >= from) && (to == null || s.On <= to)).ToList();
        if (history.Count < MinimumSessions)
        {
            return new CaseAnalysis(false, sessions.Count, MinimumSessions, null, null, null, [], absenceSessions);
        }

        var historyDayOf = history.ToDictionary(s => s.NoteId, s => s.On);
        var historyNoteIds = historyDayOf.Keys.ToList();
        var allTags = await (
            from tag in CountedTags(db)
            join term in db.ThemeTerms.AsNoTracking() on tag.TermId equals term.Id
            join r in db.ExcerptRefs.AsNoTracking().Where(r => r.ProviderId == providerId && r.SourceType == "note") on new { Id = tag.ExcerptRefId, Note = tag.NoteId } equals new { Id = (Guid?)r.Id, Note = r.SourceId } into refs
            from r in refs.DefaultIfEmpty()
            where historyNoteIds.Contains(tag.NoteId)
            select new
            {
                tag.NoteId, tag.CreatedAt, term.Id, term.Code, term.Label, term.ParentTermId, term.Status,
                RefId = (Guid?)r.Id, r.SectionKey, r.Quote,
            })
            .ToListAsync(ct);

        // Every listed child brings its parent (and its ancestors) even without occurrences, so the UI can nest.
        var info = allTags.GroupBy(t => t.Id).Select(g => g.First())
            .ToDictionary(t => t.Id, t => new TermInfo(t.Id, t.Code, t.Label, t.ParentTermId, t.Status));
        var missing = info.Values.Where(t => t.ParentId != null).Select(t => t.ParentId!.Value).Distinct().Where(id => !info.ContainsKey(id)).ToList();
        while (missing.Count > 0)
        {
            var loaded = await db.ThemeTerms.AsNoTracking().Where(t => missing.Contains(t.Id)).ToListAsync(ct);
            foreach (var t in loaded)
            {
                info[t.Id] = new TermInfo(t.Id, t.Code, t.Label, t.ParentTermId, t.Status);
            }
            missing = loaded.Where(t => t.ParentTermId != null).Select(t => t.ParentTermId!.Value).Distinct().Where(id => !info.ContainsKey(id)).ToList();
        }

        // Absences: the whole history, every known term (parents without records included, to follow the links).
        var recordsOf = allTags
            .GroupBy(t => t.Id)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<AbsenceRecord>)g.OrderBy(t => historyDayOf[t.NoteId]).ThenBy(t => t.CreatedAt).ThenBy(t => t.NoteId)
                    .Select(t => new AbsenceRecord(t.NoteId, t.RefId, t.SectionKey, t.Quote)).ToList());
        var absences = ThemeAbsence.Detect(
            history.Select(s => new AbsenceSession(s.NoteId, s.On)).ToList(),
            info.Values.Select(t => new AbsenceTerm(t.Id, t.Code, t.Label, t.ParentId, t.Status == "active", recordsOf.GetValueOrDefault(t.Id) ?? [])).ToList(),
            absenceSessions, today ?? PatientSearchQuery.PracticeToday(DateTimeOffset.UtcNow));

        if (sessions.Count < MinimumSessions)
        {
            return new CaseAnalysis(false, sessions.Count, MinimumSessions, null, null, null, absences, absenceSessions);
        }

        var dayOf = sessions.ToDictionary(s => s.NoteId, s => s.On);
        var tags = allTags.Where(t => dayOf.ContainsKey(t.NoteId)).ToList();
        var termIds = tags.Select(t => t.Id).Distinct().ToList();
        var parents = new List<TermInfo>();
        var seen = termIds.ToHashSet();
        foreach (var id in termIds.ToList())
        {
            for (var parent = info[id].ParentId; parent is { } p && seen.Add(p); parent = info[p].ParentId)
            {
                parents.Add(info[p]);
            }
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
                p.Id, p.Code, p.Label, p.ParentId, p.Status == "active", supersededBy.GetValueOrDefault(p.Id) ?? [], [])))
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


        return new CaseAnalysis(true, sessions.Count, MinimumSessions, sessions, themes, cooccurrence, absences, absenceSessions);
    }

    // Tags that count as a record: not removed and not an untouched AI proposal (CreatedBy 'ai' with no AddedById).
    internal static IQueryable<ThemeTag> CountedTags(AnamnysDbContext db) =>
        db.ThemeTags.AsNoTracking().Where(tag => tag.RemovedAt == null && !(tag.CreatedBy == "ai" && tag.AddedById == null));

    // The patient's notes (only the signed ones with signedOnly) dated by the session date rule: the session's actual
    // start, else its appointment's start, else the signing day, else the creation; ordered by day, instant and id.
    internal static async Task<List<DatedNote>> DatedNotesAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, bool signedOnly, CancellationToken ct)
    {
        var notes = await db.Notes.AsNoTracking()
            .Where(n => n.ProviderId == providerId && n.PatientId == patientId && (!signedOnly || Signed.Contains(n.Status)))
            .Select(n => new { n.Id, n.SessionId, n.SignedAt, n.CreatedAt, n.Status })
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

        return notes
            .Select(n =>
            {
                appointments.TryGetValue(n.SessionId ?? Guid.Empty, out var appointment);
                var at = n.SessionId is { } sid && actualStarts.TryGetValue(sid, out var start) ? start
                    : appointment?.StartsAt ?? n.SignedAt ?? n.CreatedAt;
                return new DatedNote(n.Id, PatientSearchQuery.PracticeToday(at), at, n.SessionId, appointment?.Id, Signed.Contains(n.Status));
            })
            .OrderBy(n => n.On).ThenBy(n => n.At).ThenBy(n => n.NoteId)
            .ToList();
    }
}

internal sealed record DatedNote(Guid NoteId, DateOnly On, DateTimeOffset At, Guid? SessionId, Guid? AppointmentId, bool Signed);
