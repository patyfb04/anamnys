using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Documents;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Retention;

public sealed record UnsignedNoteItem(Guid NoteId, Guid PatientId, string PatientName, DateTimeOffset CreatedAt, int AgeDays);

public sealed record RetentionItem(
    Guid PatientId, string PatientName, DateOnly LastRecordAt, DateOnly RetentionUntil, bool Expired, bool HasFutureAppointment);

public sealed record DocumentComplianceItem(
    Guid DocumentId, Guid PatientId, string PatientName, string Kind, string Status, IReadOnlyList<string> Missing, DateTimeOffset CreatedAt);

public sealed record CompliancePanel(
    IReadOnlyList<UnsignedNoteItem> UnsignedNotes, IReadOnlyList<RetentionItem> Retention, IReadOnlyList<DocumentComplianceItem> Documents);

// The "Conformidade" page (design/specs/2026-10-28-retention-panel-design.md §3). Three lists, each ordered by
// date, scoped to the provider, without discarded patients. Days are practice-local.
public static class ComplianceQuery
{
    public const int RetentionWindowDays = 90;

    public static async Task<CompliancePanel> GetAsync(AnamnysDbContext db, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        var threshold = await db.Providers.AsNoTracking().Where(p => p.Id == providerId).Select(p => p.UnsignedNoteDays).SingleAsync(ct);
        return new CompliancePanel(
            await UnsignedNotesAsync(db, providerId, threshold, now, ct),
            await RetentionAsync(db, providerId, now, ct),
            await DocumentsAsync(db, providerId, ct));
    }

    // Notes without a signature that are at least `thresholdDays` practice days old, oldest first.
    public static async Task<IReadOnlyList<UnsignedNoteItem>> UnsignedNotesAsync(
        AnamnysDbContext db, Guid providerId, int thresholdDays, DateTimeOffset now, CancellationToken ct)
    {
        var today = PatientSearchQuery.PracticeToday(now);
        // A note N practice days old is at least N - 1 days old in elapsed time; the exact day count is taken below.
        var bound = now.ToUniversalTime().AddDays(-(thresholdDays - 1));
        var rows = await (
                from n in db.Notes.AsNoTracking()
                join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
                where n.ProviderId == providerId && p.ProviderId == providerId && p.DiscardedAt == null
                    && n.SignedAt == null && n.CreatedAt <= bound
                orderby n.CreatedAt, n.Id
                select new { n.Id, n.PatientId, Name = p.FirstName + " " + p.LastName, n.CreatedAt })
            .ToListAsync(ct);
        return rows
            .Select(r => new UnsignedNoteItem(r.Id, r.PatientId, r.Name, r.CreatedAt, today.DayNumber - PatientSearchQuery.PracticeToday(r.CreatedAt).DayNumber))
            .Where(i => i.AgeDays >= thresholdDays)
            .ToList();
    }

    // Records whose deadline is within the window or already past, earliest deadline first. Expired means the
    // deadline day has gone by; a future appointment is one still scheduled or confirmed.
    public static async Task<IReadOnlyList<RetentionItem>> RetentionAsync(
        AnamnysDbContext db, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        var today = PatientSearchQuery.PracticeToday(now);
        var until = today.AddDays(RetentionWindowDays);
        var nowUtc = now.ToUniversalTime();
        var rows = await db.Patients.AsNoTracking()
            .Where(p => p.ProviderId == providerId && p.DiscardedAt == null
                && p.LastRecordAt != null && p.RetentionUntil != null && p.RetentionUntil <= until)
            .OrderBy(p => p.RetentionUntil).ThenBy(p => p.Id)
            .Select(p => new
            {
                p.Id,
                Name = p.FirstName + " " + p.LastName,
                p.LastRecordAt,
                p.RetentionUntil,
                Future = db.Appointments.Any(a => a.ProviderId == providerId && a.PatientId == p.Id && a.StartsAt > nowUtc
                    && (a.Status == AppointmentStatus.Scheduled || a.Status == AppointmentStatus.Confirmed)),
            })
            .ToListAsync(ct);
        return rows
            .Select(r => new RetentionItem(r.Id, r.Name, r.LastRecordAt!.Value, r.RetentionUntil!.Value, r.RetentionUntil < today, r.Future))
            .ToList();
    }

    // Drafts (with what is still missing for the current ruleset and profile) and signed documents whose
    // signature check did not pass (advisory signature with missing elements). Superseded documents are left out.
    // Only the columns the list needs; the latest check of each signed document is picked in SQL.
    public static async Task<IReadOnlyList<DocumentComplianceItem>> DocumentsAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct)
    {
        var provider = await db.Providers.AsNoTracking()
            .Where(p => p.Id == providerId)
            .Select(p => new { p.CrpNumber, p.CrpRegion, p.PracticeCity })
            .SingleAsync(ct);
        var hasCrp = !string.IsNullOrWhiteSpace(provider.CrpNumber) && !string.IsNullOrWhiteSpace(provider.CrpRegion);
        var hasCity = !string.IsNullOrWhiteSpace(provider.PracticeCity);

        var open =
            from d in db.ClinicalDocuments.AsNoTracking()
            join p in db.Patients.AsNoTracking() on d.PatientId equals p.Id
            where d.ProviderId == providerId && p.ProviderId == providerId && p.DiscardedAt == null && d.SupersededBy == null
            select new { Document = d, Name = p.FirstName + " " + p.LastName };

        var drafts = await open
            .Where(x => x.Document.SignedAt == null)
            .Select(x => new
            {
                x.Document.Id, x.Document.PatientId, x.Name, x.Document.Kind, x.Document.Fields, x.Document.Confirmations,
                x.Document.Team, x.Document.RulesetId, x.Document.CreatedAt,
            })
            .ToListAsync(ct);
        var rulesets = await db.ConformityRulesets.AsNoTracking()
            .Where(r => open.Any(x => x.Document.SignedAt == null && x.Document.RulesetId == r.Id))
            .ToDictionaryAsync(r => r.Id, ct);

        var failed = await (
                from x in open
                where x.Document.SignedAt != null
                let latestId = db.ConformityChecks
                    .Where(c => c.DocumentId == x.Document.Id)
                    .OrderByDescending(c => c.CheckedAt).ThenByDescending(c => c.Id)
                    .Select(c => (Guid?)c.Id)
                    .FirstOrDefault()
                join c in db.ConformityChecks.AsNoTracking() on latestId equals (Guid?)c.Id
                where !c.Passed
                select new { x.Document.Id, x.Document.PatientId, x.Name, x.Document.Kind, x.Document.CreatedAt, c.MissingElements })
            .ToListAsync(ct);

        var items = new List<DocumentComplianceItem>(drafts.Count + failed.Count);
        foreach (var d in drafts)
        {
            IReadOnlyList<string> missing = d.RulesetId is { } rid && rulesets.TryGetValue(rid, out var ruleset)
                ? DocumentConformity.Check(new ConformityInput(
                    d.Kind, ruleset.Elements, ruleset.Prohibitions, d.Fields, d.Confirmations, hasCrp, hasCity, d.Team)).MissingElements
                : [];
            items.Add(new DocumentComplianceItem(d.Id, d.PatientId, d.Name, d.Kind, "draft", missing, d.CreatedAt));
        }
        items.AddRange(failed.Select(f => new DocumentComplianceItem(f.Id, f.PatientId, f.Name, f.Kind, "signed", f.MissingElements, f.CreatedAt)));
        return items.OrderBy(i => i.CreatedAt).ThenBy(i => i.DocumentId).ToList();
    }
}
