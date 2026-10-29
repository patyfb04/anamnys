using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Medications;

public sealed record TimelineOrigin(
    string Kind, Guid? DocumentId = null, string? DocumentKind = null, DateOnly? DocumentDate = null, int? Page = null, Guid? ExcerptId = null);

public sealed record TimelineSegment(
    Guid MedicationId, string? Dose, string? Posology, DateOnly StartedOn, DateOnly? EndedOn, TimelineOrigin Origin);

public sealed record TimelineMedication(Guid TreatmentId, string Drug, IReadOnlyList<TimelineSegment> Segments);

public sealed record TimelinePoint(Guid ApplicationId, int Score, DateOnly AppliedOn);

public sealed record TimelineScale(Guid InstrumentId, string Code, string Name, int MinScore, int MaxScore, IReadOnlyList<TimelinePoint> Points);

public sealed record TimelineAppointment(Guid Id, DateOnly On, string Kind);

public sealed record PatientTimeline(
    IReadOnlyList<TimelineMedication> Medications, IReadOnlyList<TimelineScale> Scales, IReadOnlyList<TimelineAppointment> Appointments);

// design/specs/2026-10-27-medication-timeline-design.md §3: medication lanes (one per treatment, a segment per
// record) and applied-scale series for one patient, optionally within a period of dates. Dates only; no
// document name, fact text or answers leave here.
public static class MedicationTimeline
{
    public const string Manual = "manual";
    public const string Document = "document";
    public const string DocumentDeleted = "document_deleted";

    public static async Task<PatientTimeline?> GetAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateOnly? from, DateOnly? to, CancellationToken ct, DateTimeOffset? now = null)
    {
        if (!await db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null, ct))
        {
            return null;
        }

        var entries = await db.MedicationEntries.AsNoTracking()
            .Where(m => m.PatientId == patientId
                && (to == null || m.StartedOn <= to)
                && (from == null || m.EndedOn == null || m.EndedOn >= from))
            .ToListAsync(ct);

        var factIds = entries.Where(m => m.SourceFactId != null).Select(m => m.SourceFactId!.Value).ToList();
        var sources = factIds.Count == 0
            ? []
            : await (
                from f in db.ExtractedFacts.AsNoTracking()
                join d in db.ExternalDocuments.AsNoTracking() on f.ExternalDocumentId equals d.Id
                where factIds.Contains(f.Id) && f.ProviderId == providerId && d.ProviderId == providerId && d.PatientId == patientId
                    && d.DeletedAt == null
                select new { f.Id, DocumentId = d.Id, DocumentKind = d.Kind, d.DocumentDate, d.UploadedAt, Page = f.PageRef, ExcerptId = f.ExcerptRefId })
                .ToDictionaryAsync(r => r.Id, ct);

        TimelineOrigin OriginOf(Data.Entities.MedicationEntry m)
        {
            if (!m.FromDocument)
            {
                return new TimelineOrigin(Manual);
            }
            return m.SourceFactId is { } id && sources.TryGetValue(id, out var s)
                ? new TimelineOrigin(
                    Document, s.DocumentId, s.DocumentKind,
                    s.DocumentDate ?? Patients.PatientSearchQuery.PracticeToday(s.UploadedAt), s.Page, s.ExcerptId)
                : new TimelineOrigin(DocumentDeleted);
        }

        var medications = entries
            .GroupBy(m => m.TreatmentId)
            .Select(g =>
            {
                var segments = g.OrderBy(m => m.StartedOn).ThenBy(m => m.CreatedAt).ThenBy(m => m.Id).ToList();
                return new
                {
                    First = segments[0].StartedOn,
                    Medication = new TimelineMedication(
                        g.Key, segments[^1].Drug,
                        segments.Select(m => new TimelineSegment(m.Id, m.Dose, m.Posology, m.StartedOn, m.EndedOn, OriginOf(m))).ToList()),
                };
            })
            .OrderBy(x => x.First).ThenBy(x => x.Medication.Drug, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Medication.TreatmentId)
            .Select(x => x.Medication)
            .ToList();

        var applications = await (
            from a in db.ScaleApplications.AsNoTracking()
            join i in db.Instruments on a.InstrumentId equals i.Id
            where a.PatientId == patientId && a.RemovedAt == null
            select new { a.Id, a.Score, a.AppliedAt, InstrumentId = i.Id, i.Code, i.Name, i.MinScore, i.MaxScore })
            .ToListAsync(ct);
        var scales = applications
            .Select(a => new { a, On = Patients.PatientSearchQuery.PracticeToday(a.AppliedAt) })
            .Where(x => (from == null || x.On >= from) && (to == null || x.On <= to))
            .GroupBy(x => new { x.a.InstrumentId, x.a.Code, x.a.Name, x.a.MinScore, x.a.MaxScore })
            .OrderBy(g => g.Key.Code, StringComparer.Ordinal)
            .Select(g => new TimelineScale(
                g.Key.InstrumentId, g.Key.Code, g.Key.Name, g.Key.MinScore, g.Key.MaxScore,
                g.OrderBy(x => x.a.AppliedAt).ThenBy(x => x.a.Id).Select(x => new TimelinePoint(x.a.Id, x.a.Score, x.On)).ToList()))
            .ToList();

        var appointments = (await Attendance.AttendanceQuery.EventsAsync(db, providerId, patientId, from, to, now ?? DateTimeOffset.UtcNow, ct))!
            .Select(e => new TimelineAppointment(e.Id, e.On, e.Kind))
            .ToList();

        return new PatientTimeline(medications, scales, appointments);
    }
}
