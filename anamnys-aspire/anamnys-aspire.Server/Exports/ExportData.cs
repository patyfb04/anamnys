using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Exports;

public sealed record ExportPatient(Patient Patient, string Folder);

// Column subset of a note: RawTranscript is never selected, so nothing built from this can carry it.
public sealed record ExportNote(
    Guid Id, Guid PatientId, Guid? SessionId, string Status, string Format, DateTimeOffset? SignedAt,
    string? SignedByCrp, string? PdfObjectKey, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ExportTheme(
    Guid NoteId, string Code, string Label, string? ParentLabel, string Origin, bool Proposed, DateTimeOffset AddedAt);

public sealed record ExportScale(
    Guid Id, Guid PatientId, string Code, string Name, int Score, int MinScore, int MaxScore,
    DateTimeOffset AppliedAt, Guid? SessionId);

public sealed record ExportObjective(
    Guid PatientId, Guid PlanId, DateTimeOffset PlanCreatedAt, DateTimeOffset? PlanReviewedAt, DateTimeOffset? PlanClosedAt,
    Guid? ObjectiveId, string? Description, DateTimeOffset? CreatedAt);

// Previous/new values are never selected (spec 2026-10-23 §1).
public sealed record ExportAudit(DateTimeOffset At, string Action, string? Field, Guid? NoteId, Guid? DocumentId);

// A disposal term (F-06) with the name kept on the discarded patient row.
public sealed record ExportTerm(DisposalTerm Term, string PatientName);

public sealed record AccountData(
    Provider Provider,
    IReadOnlyList<ExportPatient> Patients,
    IReadOnlyList<ExportNote> Notes,
    ILookup<Guid, NoteSection> SectionsByNote,
    IReadOnlyDictionary<Guid, Session> Sessions,
    ILookup<Guid, ExportTheme> ThemesByNote,
    IReadOnlyList<ClinicalDocument> Documents,
    IReadOnlyDictionary<Guid, ConformityRuleset> Rulesets,
    ILookup<Guid, PatientDiagnosis> DiagnosesByPatient,
    ILookup<Guid, MedicationEntry> MedicationsByPatient,
    ILookup<Guid, ExportObjective> ObjectivesByPatient,
    ILookup<Guid, ExportScale> ScalesByPatient,
    IReadOnlyList<Appointment> Appointments,
    IReadOnlyList<ExportAudit> Audit,
    IReadOnlyList<ExportTerm> Terms);

// Everything the export shows, read once and scoped to the provider on every query. PDFs are not
// loaded here: the builder reads or renders them one at a time.
public static class ExportData
{
    public static async Task<AccountData> LoadAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct)
    {
        var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);

        // A discarded record appears only through its disposal term (design/specs/2026-10-28-retention-panel-design.md §3).
        var patients = await db.Patients.AsNoTracking().Where(p => p.ProviderId == providerId && p.DiscardedAt == null).ToListAsync(ct);

        var notes = await db.Notes.AsNoTracking()
            .Where(n => n.ProviderId == providerId)
            .Select(n => new ExportNote(
                n.Id, n.PatientId, n.SessionId, n.Status, n.Format, n.SignedAt, n.SignedByCrp, n.PdfObjectKey,
                n.CreatedAt, n.UpdatedAt))
            .ToListAsync(ct);

        var sections = await (
            from s in db.NoteSections.AsNoTracking()
            join n in db.Notes on s.NoteId equals n.Id
            where n.ProviderId == providerId
            select s).ToListAsync(ct);

        var sessions = await db.Sessions.AsNoTracking().Where(s => s.ProviderId == providerId).ToDictionaryAsync(s => s.Id, ct);

        var tags = await (
            from tag in db.ThemeTags.AsNoTracking()
            join n in db.Notes on tag.NoteId equals n.Id
            join term in db.ThemeTerms.AsNoTracking() on tag.TermId equals term.Id
            where n.ProviderId == providerId && tag.RemovedAt == null
            select new { tag.NoteId, term.Code, term.Label, term.ParentTermId, tag.CreatedBy, tag.AddedById, tag.CreatedAt })
            .ToListAsync(ct);
        var parentIds = tags.Where(t => t.ParentTermId != null).Select(t => t.ParentTermId!.Value).Distinct().ToList();
        var parents = await db.ThemeTerms.AsNoTracking()
            .Where(t => parentIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Label, ct);
        var themes = tags
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Label, StringComparer.Ordinal)
            .Select(t => new ExportTheme(
                t.NoteId, t.Code, t.Label, t.ParentTermId is { } parent ? parents.GetValueOrDefault(parent) : null,
                t.CreatedBy, t.CreatedBy == "ai" && t.AddedById is null, t.CreatedAt))
            .ToLookup(t => t.NoteId);

        var documents = await db.ClinicalDocuments.AsNoTracking().Where(d => d.ProviderId == providerId).ToListAsync(ct);
        var rulesetIds = documents.Where(d => d.RulesetId != null).Select(d => d.RulesetId!.Value).Distinct().ToList();
        var rulesets = await db.ConformityRulesets.AsNoTracking()
            .Where(r => rulesetIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct);

        var diagnoses = await (
            from d in db.PatientDiagnoses.AsNoTracking()
            join p in db.Patients on d.PatientId equals p.Id
            where p.ProviderId == providerId
            select d).ToListAsync(ct);

        var medications = await (
            from m in db.MedicationEntries.AsNoTracking()
            join p in db.Patients on m.PatientId equals p.Id
            where p.ProviderId == providerId
            select m).ToListAsync(ct);

        var plans = await (
            from plan in db.TreatmentPlans.AsNoTracking()
            join p in db.Patients on plan.PatientId equals p.Id
            where p.ProviderId == providerId
            select plan).ToListAsync(ct);
        var objectives = await (
            from o in db.PlanObjectives.AsNoTracking()
            join plan in db.TreatmentPlans on o.PlanId equals plan.Id
            join p in db.Patients on plan.PatientId equals p.Id
            where p.ProviderId == providerId
            select o).ToListAsync(ct);
        var objectivesByPlan = objectives.ToLookup(o => o.PlanId);
        var planRows = plans
            .OrderBy(plan => plan.CreatedAt)
            .SelectMany(plan => objectivesByPlan[plan.Id].Any()
                ? objectivesByPlan[plan.Id].OrderBy(o => o.CreatedAt).Select(o => new ExportObjective(
                    plan.PatientId, plan.Id, plan.CreatedAt, plan.ReviewedAt, plan.ClosedAt, o.Id, o.Description, o.CreatedAt))
                : new[] { new ExportObjective(plan.PatientId, plan.Id, plan.CreatedAt, plan.ReviewedAt, plan.ClosedAt, null, null, null) })
            .ToLookup(o => o.PatientId);

        var scales = await (
            from a in db.ScaleApplications.AsNoTracking()
            join p in db.Patients on a.PatientId equals p.Id
            join i in db.Instruments on a.InstrumentId equals i.Id
            where p.ProviderId == providerId && a.RemovedAt == null
            orderby a.AppliedAt
            select new ExportScale(a.Id, a.PatientId, i.Code, i.Name, a.Score, i.MinScore, i.MaxScore, a.AppliedAt, a.SessionId))
            .ToListAsync(ct);

        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => a.ProviderId == providerId)
            .OrderBy(a => a.StartsAt)
            .ToListAsync(ct);

        var audit = await db.AuditEntries.AsNoTracking()
            .Where(a =>
                (a.NoteId != null && db.Notes.Any(n => n.Id == a.NoteId && n.ProviderId == providerId))
                || (a.DocumentId != null && db.ClinicalDocuments.Any(d => d.Id == a.DocumentId && d.ProviderId == providerId))
                || (a.NoteId == null && a.DocumentId == null && a.ActorId == providerId))
            .OrderBy(a => a.At).ThenBy(a => a.Id)
            .Select(a => new ExportAudit(a.At, a.Action, a.FieldChanged, a.NoteId, a.DocumentId))
            .ToListAsync(ct);

        var terms = await (
                from t in db.DisposalTerms.AsNoTracking()
                join p in db.Patients.AsNoTracking() on t.PatientId equals p.Id
                where t.ProviderId == providerId && p.ProviderId == providerId
                orderby t.CreatedAt, t.Id
                select new ExportTerm(t, p.FirstName + " " + p.LastName))
            .ToListAsync(ct);

        return new AccountData(
            provider,
            AssignFolders(patients),
            notes,
            sections.ToLookup(s => s.NoteId),
            sessions,
            themes,
            documents,
            rulesets,
            diagnoses.ToLookup(d => d.PatientId),
            medications.ToLookup(m => m.PatientId),
            planRows,
            scales.ToLookup(s => s.PatientId),
            appointments,
            audit,
            terms);
    }

    // NNN = position by name, XX = initials: the folder never carries the patient's name.
    public static IReadOnlyList<ExportPatient> AssignFolders(IEnumerable<Patient> patients)
    {
        var byName = StringComparer.Create(ExportFormat.PtBr, ignoreCase: true);
        return patients
            .OrderBy(p => p.FirstName, byName).ThenBy(p => p.LastName, byName).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .Select((p, i) => new ExportPatient(p, $"{i + 1:000}-{ExportFormat.Initial(p.FirstName)}{ExportFormat.Initial(p.LastName)}"))
            .ToList();
    }
}
