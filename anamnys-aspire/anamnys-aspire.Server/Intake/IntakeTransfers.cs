using System.Globalization;
using Anamnys.Server.Availability;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Excerpts;
using Anamnys.Server.Notes;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Intakes;

// "Levar para o prontuário" (design/specs/2026-11-12-structured-intake-design.md §3 Transfers). Transfers exist only
// for signed intakes; confirm writes the destination, an excerpt reference into the signed note's section holding the
// answer (F-03) and the audit entry in one transaction; ignore writes nothing to the record.
public static class IntakeTransfers
{
    public static async Task<IReadOnlyList<IntakeTransferView>?> ListAsync(AnamnysDbContext db, Guid providerId, Guid id, CancellationToken ct)
    {
        var intake = await IntakeService.Owned(db, providerId).AsNoTracking()
            .Where(i => i.Id == id).Select(i => new { i.PatientId }).SingleOrDefaultAsync(ct);
        if (intake is null)
        {
            return null;
        }
        var patient = await db.Patients.AsNoTracking()
            .Where(p => p.Id == intake.PatientId)
            .Select(p => new { p.DateOfBirth, p.Phone, p.ContactEmail })
            .SingleAsync(ct);
        var rows = await (
            from t in db.IntakeTransfers.AsNoTracking()
            join q in db.IntakeQuestions.AsNoTracking() on t.QuestionId equals q.Id
            where t.IntakeId == id
            orderby q.Position, t.Id
            select new { Transfer = t, q.Label, q.SectionKey }).ToListAsync(ct);
        return [.. rows.Select(r => new IntakeTransferView(
            r.Transfer.Id, r.Transfer.QuestionId, r.Label, r.SectionKey, r.Transfer.TargetField, r.Transfer.Value,
            r.Transfer.TargetField switch
            {
                IntakeRules.DateOfBirth => patient.DateOfBirth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                IntakeRules.Phone => patient.Phone,
                IntakeRules.ContactEmail => patient.ContactEmail,
                _ => null,
            },
            r.Transfer.Status, r.Transfer.DecidedAt, r.Transfer.ResultId))];
    }

    public static Task<IntakeResult<IntakeTransferView>> ConfirmAsync(
        AnamnysDbContext db, Guid providerId, Guid id, Guid transferId, DateTimeOffset now, CancellationToken ct) =>
        DecideAsync(db, providerId, id, transferId, confirm: true, now, ct);

    public static Task<IntakeResult<IntakeTransferView>> IgnoreAsync(
        AnamnysDbContext db, Guid providerId, Guid id, Guid transferId, DateTimeOffset now, CancellationToken ct) =>
        DecideAsync(db, providerId, id, transferId, confirm: false, now, ct);

    private static async Task<IntakeResult<IntakeTransferView>> DecideAsync(
        AnamnysDbContext db, Guid providerId, Guid id, Guid transferId, bool confirm, DateTimeOffset now, CancellationToken ct)
    {
        var result = new IntakeResult<IntakeTransferView>(IntakeOutcome.NotFound);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            result = new(IntakeOutcome.NotFound);
            db.ChangeTracker.Clear();
            var seen = await IntakeService.Owned(db, providerId).AsNoTracking()
                .Where(i => i.Id == id && db.IntakeTransfers.Any(t => t.Id == transferId && t.IntakeId == i.Id))
                .Select(i => new { i.NoteId })
                .SingleOrDefaultAsync(ct);
            if (seen is null)
            {
                return;
            }
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (seen.NoteId is { } noteId)
            {
                // An excerpt reference is added: the patient row first, like every such write (NoteLocks).
                await NoteLocks.LockPatientAsync(db, providerId, noteId, ct);
            }
            await IntakeService.LockAsync(db, id, ct);
            var intake = await IntakeService.Owned(db, providerId).SingleOrDefaultAsync(i => i.Id == id, ct);
            var transfer = intake is null
                ? null
                : await db.IntakeTransfers.SingleOrDefaultAsync(t => t.Id == transferId && t.IntakeId == id, ct);
            if (intake is null || transfer is null)
            {
                return;
            }
            if (intake.Status != IntakeStatus.Signed || intake.NoteId is null || intake.NoteId != seen.NoteId)
            {
                result = new(IntakeOutcome.NotSigned);
                return;
            }
            if (transfer.Status != IntakeTransferStatus.Pending)
            {
                result = new(IntakeOutcome.NotPending);
                return;
            }
            var patient = await db.Patients.SingleAsync(p => p.Id == intake.PatientId && p.ProviderId == providerId, ct);
            if (confirm && patient.ArchivedAt is not null)
            {
                result = new(IntakeOutcome.PatientArchived);
                return;
            }
            var question = await db.IntakeQuestions.AsNoTracking().SingleAsync(q => q.Id == transfer.QuestionId, ct);

            if (confirm)
            {
                var excerpt = await ExcerptService.AddAsync(db, providerId, intake.NoteId.Value, question.SectionKey,
                    IntakeService.Display(question.AnswerType, transfer.Value), null, null, ct);
                if (excerpt.Outcome != ExcerptOutcome.Ok)
                {
                    // The signed note lost the section: nothing can point back to the answer (F-03), so nothing is written.
                    result = new(IntakeOutcome.NotSigned);
                    return;
                }
                transfer.ResultId = await WriteAsync(db, patient, transfer, intake.NoteId.Value, intake.SignedAt!.Value, now, ct);
                transfer.ExcerptRefId = excerpt.Ref!.Id;
                transfer.Status = IntakeTransferStatus.Confirmed;
                IntakeRules.Audit(db, providerId, IntakeRules.TransferConfirmedAction,
                    $"{transfer.Id} intake {intake.Id} {transfer.TargetField} {transfer.ResultId}", now);
            }
            else
            {
                transfer.Status = IntakeTransferStatus.Ignored;
                IntakeRules.Audit(db, providerId, IntakeRules.TransferIgnoredAction, $"{transfer.Id} intake {intake.Id} {transfer.TargetField}", now);
            }
            transfer.DecidedAt = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            string? current = transfer.TargetField switch
            {
                IntakeRules.DateOfBirth => patient.DateOfBirth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                IntakeRules.Phone => patient.Phone,
                IntakeRules.ContactEmail => patient.ContactEmail,
                _ => null,
            };
            result = new(IntakeOutcome.Ok, new IntakeTransferView(transfer.Id, transfer.QuestionId, question.Label, question.SectionKey,
                transfer.TargetField, transfer.Value, current, transfer.Status, transfer.DecidedAt, transfer.ResultId));
        });
        return result;
    }

    // Writes the answer to its destination and returns the row written (the patient for registration fields).
    private static async Task<Guid> WriteAsync(
        AnamnysDbContext db, Patient patient, IntakeTransfer transfer, Guid noteId, DateTimeOffset signedAt, DateTimeOffset now, CancellationToken ct)
    {
        switch (transfer.TargetField)
        {
            case IntakeRules.DateOfBirth:
                patient.DateOfBirth = DateOnly.ParseExact(transfer.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                patient.UpdatedAt = now;
                return patient.Id;
            case IntakeRules.Phone:
                patient.Phone = transfer.Value.Trim();
                patient.UpdatedAt = now;
                return patient.Id;
            case IntakeRules.ContactEmail:
                patient.ContactEmail = transfer.Value.Trim();
                patient.UpdatedAt = now;
                return patient.Id;
            case IntakeRules.Medication:
                var medicationId = Guid.NewGuid();
                db.MedicationEntries.Add(new MedicationEntry
                {
                    Id = medicationId,
                    TreatmentId = medicationId,
                    PatientId = patient.Id,
                    Drug = transfer.Value.Trim(),
                    StartedOn = AvailabilityTime.LocalDate(signedAt, AvailabilityTime.Provider),
                    SourceNoteId = noteId,
                    CreatedAt = now,
                });
                return medicationId;
            case IntakeRules.Diagnosis:
                var diagnosisId = Guid.NewGuid();
                db.PatientDiagnoses.Add(new PatientDiagnosis
                {
                    Id = diagnosisId,
                    PatientId = patient.Id,
                    Description = transfer.Value.Trim(),
                    RecordedAt = now,
                });
                return diagnosisId;
            case IntakeRules.Objective:
                var plan = await PatientRecords.OpenPlanAsync(db, patient.Id, ct);
                if (plan is null)
                {
                    plan = new TreatmentPlan { Id = Guid.NewGuid(), PatientId = patient.Id, CreatedAt = now };
                    db.TreatmentPlans.Add(plan);
                }
                var objectiveId = Guid.NewGuid();
                db.PlanObjectives.Add(new PlanObjective
                {
                    Id = objectiveId,
                    PlanId = plan.Id,
                    Description = transfer.Value.Trim(),
                    CreatedAt = now,
                });
                return objectiveId;
            default:
                throw new InvalidOperationException($"Unknown intake target field '{transfer.TargetField}'.");
        }
    }
}
