using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Profile;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.Patients;

public enum RecordOutcome { Ok, NotFound, Conflict }

// Provider-scoped patient record operations. See
// design/specs/2026-10-01-patient-records-design.md §4. Every call resolves the patient
// with Id == patientId && ProviderId == providerId; clinical items are reached only through
// an owned patient, so a foreign id is indistinguishable from a missing one.
public static class PatientRecords
{
    private const int RecentNotesCount = 10;

    public static async Task<Guid> CreateAsync(
        AnamnysDbContext db, Guid providerId, CreatePatientRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nowUtc = now.ToUniversalTime();
        var patient = new Patient
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            FirstName = ProfileText.Clean(request.FirstName)!,
            LastName = ProfileText.Clean(request.LastName)!,
            ContactEmail = ProfileText.Clean(request.ContactEmail),
            DateOfBirth = request.DateOfBirth,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
        };
        db.Patients.Add(patient);

        foreach (var diagnosis in request.Diagnoses ?? [])
        {
            db.PatientDiagnoses.Add(NewDiagnosis(patient.Id, diagnosis, nowUtc));
        }
        foreach (var medication in request.Medications ?? [])
        {
            db.MedicationEntries.Add(NewMedication(patient.Id, medication));
        }

        var objectives = request.TreatmentObjectives ?? [];
        if (objectives.Length > 0)
        {
            var plan = new TreatmentPlan { Id = Guid.NewGuid(), PatientId = patient.Id, CreatedAt = nowUtc };
            db.TreatmentPlans.Add(plan);
            // Distinct timestamps keep the provider's order when listed by CreatedAt.
            for (var i = 0; i < objectives.Length; i++)
            {
                db.PlanObjectives.Add(NewObjective(plan.Id, objectives[i], nowUtc.AddTicks(i * 10)));
            }
        }

        // One SaveChanges: EF wraps it in a single transaction.
        await db.SaveChangesAsync(cancellationToken);
        return patient.Id;
    }

    public static async Task<PatientDetailResponse?> GetAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nowUtc = now.ToUniversalTime();
        var patient = await db.Patients.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == patientId && p.ProviderId == providerId, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var nextAppointmentAt = await db.Appointments
            .Where(a => a.PatientId == patientId && a.ProviderId == providerId
                && a.StartsAt > nowUtc && (a.Status == "scheduled" || a.Status == "confirmed"))
            .MinAsync(a => (DateTimeOffset?)a.StartsAt, cancellationToken);

        var recentNotes = await db.Notes.AsNoTracking()
            .Where(n => n.PatientId == patientId && n.ProviderId == providerId)
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(RecentNotesCount)
            .Select(n => new NoteSummary(n.Id, n.Status, n.Format, n.CreatedAt, n.SignedAt))
            .ToListAsync(cancellationToken);

        var diagnoses = await db.PatientDiagnoses.AsNoTracking()
            .Where(d => d.PatientId == patientId)
            .OrderBy(d => d.ResolvedOn != null).ThenBy(d => d.RecordedAt)
            .Select(d => new DiagnosisItem(d.Id, d.Description, d.IcdCode, d.RecordedAt, d.ResolvedOn))
            .ToListAsync(cancellationToken);

        var medications = await db.MedicationEntries.AsNoTracking()
            .Where(m => m.PatientId == patientId)
            .OrderBy(m => m.EndedOn != null).ThenByDescending(m => m.StartedOn)
            .Select(m => new MedicationItem(m.Id, m.Drug, m.Dose, m.Posology, m.StartedOn, m.EndedOn))
            .ToListAsync(cancellationToken);

        var plan = await OpenPlanAsync(db, patientId, cancellationToken);
        var objectives = plan is null
            ? []
            : await db.PlanObjectives.AsNoTracking()
                .Where(o => o.PlanId == plan.Id)
                .OrderBy(o => o.CreatedAt)
                .Select(o => new ObjectiveItem(o.Id, o.Description, o.CreatedAt))
                .ToListAsync(cancellationToken);

        // The linked portal account, unless it was disabled: providers see only its login
        // email, never the personal data the person keeps on it.
        var portalEmail = patient.AccountId is { } accountId
            ? await db.PatientAccounts.AsNoTracking()
                .Where(a => a.Id == accountId && a.DisabledAt == null)
                .Select(a => a.Email)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var canDelete = patient.AccountId is null && !await HasClinicalRecordsAsync(db, patientId, cancellationToken);

        return new PatientDetailResponse(
            patient.Id,
            patient.FirstName,
            patient.LastName,
            patient.DateOfBirth,
            patient.ContactEmail,
            portalEmail,
            patient.Phone,
            portalEmail is not null,
            patient.ArchivedAt,
            patient.LastVisit,
            nextAppointmentAt,
            NoteStatusGroup.Of(recentNotes.FirstOrDefault()?.Status),
            canDelete,
            diagnoses,
            medications,
            objectives,
            recentNotes);
    }

    public static async Task<bool> UpdateAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, UpdatePatientRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var patient = await FindPatientAsync(db, providerId, patientId, cancellationToken);
        if (patient is null)
        {
            return false;
        }

        patient.FirstName = ProfileText.Clean(request.FirstName)!;
        patient.LastName = ProfileText.Clean(request.LastName)!;
        patient.ContactEmail = ProfileText.Clean(request.ContactEmail);
        patient.DateOfBirth = request.DateOfBirth;
        patient.UpdatedAt = now.ToUniversalTime();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Hard delete is for a record created by mistake: no clinical history (Res. CFP
    // 001/2009 guard) and no portal login. Everything else must be archived instead.
    public static async Task<RecordOutcome> DeleteAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking()
            .Where(p => p.Id == patientId && p.ProviderId == providerId)
            .Select(p => new { p.AccountId })
            .SingleOrDefaultAsync(cancellationToken);
        if (patient is null)
        {
            return RecordOutcome.NotFound;
        }
        if (patient.AccountId is not null || await HasClinicalRecordsAsync(db, patientId, cancellationToken))
        {
            return RecordOutcome.Conflict;
        }

        try
        {
            // Diagnoses, medications and plans cascade in the database.
            await db.Patients.Where(p => p.Id == patientId && p.ProviderId == providerId).ExecuteDeleteAsync(cancellationToken);
            return RecordOutcome.Ok;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // A clinical record arrived between the check and the delete; RESTRICT caught it.
            return RecordOutcome.Conflict;
        }
    }

    public static async Task<bool> SetArchivedAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, bool archived, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var patient = await FindPatientAsync(db, providerId, patientId, cancellationToken);
        if (patient is null)
        {
            return false;
        }

        var nowUtc = now.ToUniversalTime();
        if (archived && patient.ArchivedAt is null)
        {
            patient.ArchivedAt = nowUtc;
            patient.UpdatedAt = nowUtc;
            // An archived record cannot be invited, so its pending link stops working too.
            await PatientInvitations.RevokePendingAsync(db, patientId, nowUtc, cancellationToken);
        }
        else if (!archived && patient.ArchivedAt is not null)
        {
            patient.ArchivedAt = null;
            patient.UpdatedAt = nowUtc;
        }
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task<Guid?> AddDiagnosisAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DiagnosisInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await OwnsAsync(db, providerId, patientId, cancellationToken))
        {
            return null;
        }
        var diagnosis = NewDiagnosis(patientId, input, now.ToUniversalTime());
        db.PatientDiagnoses.Add(diagnosis);
        await db.SaveChangesAsync(cancellationToken);
        return diagnosis.Id;
    }

    public static async Task<bool> UpdateDiagnosisAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid itemId, DiagnosisInput input, CancellationToken cancellationToken)
    {
        var diagnosis = await db.PatientDiagnoses
            .SingleOrDefaultAsync(d => d.Id == itemId && d.PatientId == patientId
                && db.Patients.Any(p => p.Id == patientId && p.ProviderId == providerId), cancellationToken);
        if (diagnosis is null)
        {
            return false;
        }
        diagnosis.Description = ProfileText.Clean(input.Description)!;
        diagnosis.IcdCode = ProfileText.Clean(input.IcdCode);
        diagnosis.ResolvedOn = input.ResolvedOn;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task<bool> RemoveDiagnosisAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid itemId, CancellationToken cancellationToken) =>
        await db.PatientDiagnoses
            .Where(d => d.Id == itemId && d.PatientId == patientId
                && db.Patients.Any(p => p.Id == patientId && p.ProviderId == providerId))
            .ExecuteDeleteAsync(cancellationToken) > 0;

    public static async Task<Guid?> AddMedicationAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, MedicationInput input, CancellationToken cancellationToken)
    {
        if (!await OwnsAsync(db, providerId, patientId, cancellationToken))
        {
            return null;
        }
        var medication = NewMedication(patientId, input);
        db.MedicationEntries.Add(medication);
        await db.SaveChangesAsync(cancellationToken);
        return medication.Id;
    }

    public static async Task<bool> UpdateMedicationAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid itemId, MedicationInput input, CancellationToken cancellationToken)
    {
        var medication = await db.MedicationEntries
            .SingleOrDefaultAsync(m => m.Id == itemId && m.PatientId == patientId
                && db.Patients.Any(p => p.Id == patientId && p.ProviderId == providerId), cancellationToken);
        if (medication is null)
        {
            return false;
        }
        medication.Drug = ProfileText.Clean(input.Drug)!;
        medication.Dose = ProfileText.Clean(input.Dose);
        medication.Posology = ProfileText.Clean(input.Posology);
        medication.StartedOn = input.StartedOn!.Value;
        medication.EndedOn = input.EndedOn;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task<bool> RemoveMedicationAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid itemId, CancellationToken cancellationToken) =>
        await db.MedicationEntries
            .Where(m => m.Id == itemId && m.PatientId == patientId
                && db.Patients.Any(p => p.Id == patientId && p.ProviderId == providerId))
            .ExecuteDeleteAsync(cancellationToken) > 0;

    // Objectives live on the patient's open plan; the first objective opens one.
    public static async Task<Guid?> AddObjectiveAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, ObjectiveInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await OwnsAsync(db, providerId, patientId, cancellationToken))
        {
            return null;
        }

        var nowUtc = now.ToUniversalTime();
        var plan = await OpenPlanAsync(db, patientId, cancellationToken);
        if (plan is null)
        {
            plan = new TreatmentPlan { Id = Guid.NewGuid(), PatientId = patientId, CreatedAt = nowUtc };
            db.TreatmentPlans.Add(plan);
        }

        var objective = NewObjective(plan.Id, input.Description, nowUtc);
        db.PlanObjectives.Add(objective);
        await db.SaveChangesAsync(cancellationToken);
        return objective.Id;
    }

    public static async Task<bool> UpdateObjectiveAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid itemId, ObjectiveInput input, CancellationToken cancellationToken)
    {
        var objective = await OwnedObjectives(db, providerId, patientId, itemId).SingleOrDefaultAsync(cancellationToken);
        if (objective is null)
        {
            return false;
        }
        objective.Description = ProfileText.Clean(input.Description)!;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task<bool> RemoveObjectiveAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid itemId, CancellationToken cancellationToken) =>
        await OwnedObjectives(db, providerId, patientId, itemId).ExecuteDeleteAsync(cancellationToken) > 0;

    private static IQueryable<PlanObjective> OwnedObjectives(AnamnysDbContext db, Guid providerId, Guid patientId, Guid itemId) =>
        db.PlanObjectives.Where(o => o.Id == itemId
            && db.TreatmentPlans.Any(t => t.Id == o.PlanId && t.PatientId == patientId
                && db.Patients.Any(p => p.Id == patientId && p.ProviderId == providerId)));

    private static Task<Patient?> FindPatientAsync(AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken cancellationToken) =>
        db.Patients.SingleOrDefaultAsync(p => p.Id == patientId && p.ProviderId == providerId, cancellationToken);

    private static Task<bool> OwnsAsync(AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken cancellationToken) =>
        db.Patients.AnyAsync(p => p.Id == patientId && p.ProviderId == providerId, cancellationToken);

    private static Task<TreatmentPlan?> OpenPlanAsync(AnamnysDbContext db, Guid patientId, CancellationToken cancellationToken) =>
        db.TreatmentPlans
            .Where(t => t.PatientId == patientId && t.ClosedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    // Every table holding clinical history for the patient. The RESTRICT ones would block
    // the delete anyway; the CASCADE ones (scales, intake, consents) must not be silently
    // destroyed with it.
    private static Task<bool> HasClinicalRecordsAsync(AnamnysDbContext db, Guid patientId, CancellationToken cancellationToken) =>
        db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM "Notes" WHERE "PatientId" = {patientId})
                OR EXISTS (SELECT 1 FROM "Appointments" WHERE "PatientId" = {patientId})
                OR EXISTS (SELECT 1 FROM "Sessions" WHERE "PatientId" = {patientId})
                OR EXISTS (SELECT 1 FROM "ClinicalDocuments" WHERE "PatientId" = {patientId})
                OR EXISTS (SELECT 1 FROM "ExternalDocuments" WHERE "PatientId" = {patientId})
                OR EXISTS (SELECT 1 FROM "Dossiers" WHERE "PatientId" = {patientId})
                OR EXISTS (SELECT 1 FROM "DisclosureAuthorizations" WHERE "PatientId" = {patientId})
                OR EXISTS (SELECT 1 FROM "DisposalRecords" WHERE "PatientId" = {patientId})
                OR EXISTS (SELECT 1 FROM "ScaleApplications" WHERE "PatientId" = {patientId})
                OR EXISTS (SELECT 1 FROM "IntakeResponses" WHERE "PatientId" = {patientId})
                OR EXISTS (SELECT 1 FROM "RecordingConsents" WHERE "PatientId" = {patientId})
                AS "Value"
            """).SingleAsync(cancellationToken);

    private static PatientDiagnosis NewDiagnosis(Guid patientId, DiagnosisInput input, DateTimeOffset recordedAt) => new()
    {
        Id = Guid.NewGuid(),
        PatientId = patientId,
        Description = ProfileText.Clean(input.Description)!,
        IcdCode = ProfileText.Clean(input.IcdCode),
        RecordedAt = recordedAt,
        ResolvedOn = input.ResolvedOn,
    };

    private static MedicationEntry NewMedication(Guid patientId, MedicationInput input) => new()
    {
        Id = Guid.NewGuid(),
        PatientId = patientId,
        Drug = ProfileText.Clean(input.Drug)!,
        Dose = ProfileText.Clean(input.Dose),
        Posology = ProfileText.Clean(input.Posology),
        StartedOn = input.StartedOn!.Value,
        EndedOn = input.EndedOn,
    };

    private static PlanObjective NewObjective(Guid planId, string? description, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        PlanId = planId,
        Description = ProfileText.Clean(description)!,
        CreatedAt = createdAt,
    };
}
