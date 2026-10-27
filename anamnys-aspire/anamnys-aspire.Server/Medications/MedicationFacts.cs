using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.DocumentFacts;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Patients;
using Anamnys.Server.Profile;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Medications;

public enum MedicationFactOutcome { Ok, NotFound, Archived, NotPending, NotMedication, AlreadyRecorded, TargetNotActive, Invalid }

public sealed record ActiveMedication(Guid Id, Guid TreatmentId, string Drug, string? Dose, string? Posology, DateOnly StartedOn);

public sealed record MedicationDraftView(
    Guid FactId, string Value, string? Quote, int? Page, string Status, bool Recorded, MedicationDraft Prefill, string? Composed, IReadOnlyList<ActiveMedication> ActiveMatches);

public sealed record MedicationDraftResult(MedicationFactOutcome Outcome, MedicationDraftView? Draft = null);

public sealed record MedicationFromFactRequest(
    string? Action, Guid? TargetMedicationId, string? Drug, string? Dose, string? Posology, DateOnly? StartedOn, DateOnly? EndedOn)
{
    public const string New = "new";
    public const string DoseChange = "dose_change";
    public const string Stop = "stop";

    public Dictionary<string, string[]> Validate(DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        if (Action is not (New or DoseChange or Stop))
        {
            errors["action"] = ["Escolha o que registrar."];
            return errors;
        }
        if (Action != New && TargetMedicationId is null)
        {
            errors["targetMedicationId"] = ["Escolha o medicamento."];
        }
        if (Action == Stop)
        {
            if ((EndedOn ?? StartedOn) is not { } ended)
            {
                errors["endedOn"] = ["Informe a data de suspensão."];
            }
            else if (ended > today)
            {
                errors["endedOn"] = ["A data não pode estar no futuro."];
            }
            return errors;
        }

        PatientRules.RequireText(errors, "drug", Drug, PatientRules.MaxItemText);
        PatientRules.MaxLength(errors, "dose", Dose, 200);
        PatientRules.MaxLength(errors, "posology", Posology, 200);
        if (StartedOn is not { } started)
        {
            errors["startedOn"] = ["Informe a data de início."];
        }
        else if (started > today)
        {
            errors["startedOn"] = ["A data não pode estar no futuro."];
        }
        if (EndedOn is { } end && (end > today || end < StartedOn))
        {
            errors["endedOn"] = ["O término deve ser entre o início e hoje."];
        }
        return errors;
    }
}

public sealed record MedicationFromFactResult(MedicationFactOutcome Outcome, Guid? MedicationId = null, Dictionary<string, string[]>? Errors = null);

// design/specs/2026-10-27-medication-timeline-design.md §3. A medication fact of a received document becomes a
// record of the patient (new, a dose change that closes the active record of the drug, or a suspension). Every
// query filters by the provider, the document (not deleted) and its patient; another provider's document or
// fact is NotFound. Audit values carry ids and the action, never the drug.
public static class MedicationFacts
{
    public const string AuditAction = "medication_from_fact";
    private const string AuditField = "medication_entry";

    public static async Task<MedicationDraftResult> DraftAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, Guid factId, CancellationToken ct)
    {
        var document = await db.ExternalDocuments.AsNoTracking()
            .Where(d => d.Id == documentId && d.ProviderId == providerId && d.DeletedAt == null)
            .Select(d => new { d.PatientId, d.DocumentDate })
            .SingleOrDefaultAsync(ct);
        var fact = document is null
            ? null
            : await db.ExtractedFacts.AsNoTracking()
                .SingleOrDefaultAsync(f => f.Id == factId && f.ProviderId == providerId && f.ExternalDocumentId == documentId, ct);
        if (document is null || fact is null || !await OwnsPatientAsync(db, providerId, document.PatientId, ct))
        {
            return new MedicationDraftResult(MedicationFactOutcome.NotFound);
        }
        if (fact.Kind != FactKinds.Medication)
        {
            return new MedicationDraftResult(MedicationFactOutcome.NotMedication);
        }
        if (fact.Status == FactStatus.Discarded)
        {
            return new MedicationDraftResult(MedicationFactOutcome.NotPending);
        }

        var issueFacts = await db.ExtractedFacts.AsNoTracking()
            .Where(f => f.ExternalDocumentId == documentId && f.ProviderId == providerId && f.Kind == FactKinds.IssueDate
                && f.Status != FactStatus.Discarded)
            .Select(f => new { f.Status, f.Value, f.CorrectedValue })
            .ToListAsync(ct);
        var issueDate = issueFacts
            .OrderBy(f => f.Status == FactStatus.Pending)
            .Select(f => FactValidator.ParseDate(f.CorrectedValue ?? f.Value))
            .FirstOrDefault(d => d is not null);

        var value = fact.CorrectedValue ?? fact.Value;
        var prefill = MedicationPrefill.From(value, fact.Quote, issueDate, document.DocumentDate);
        var active = await db.MedicationEntries.AsNoTracking()
            .Where(m => m.PatientId == document.PatientId && m.EndedOn == null)
            .OrderBy(m => m.StartedOn).ThenBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(ct);
        var folded = prefill.Drug is null ? null : MedicationPrefill.Fold(prefill.Drug);
        var matches = folded is null
            ? []
            : active.Where(m => MedicationPrefill.Fold(m.Drug) == folded)
                .Select(m => new ActiveMedication(m.Id, m.TreatmentId, m.Drug, m.Dose, m.Posology, m.StartedOn))
                .ToList();
        var recorded = await db.MedicationEntries.AsNoTracking().AnyAsync(m => m.SourceFactId == factId, ct);

        return new MedicationDraftResult(
            MedicationFactOutcome.Ok,
            new MedicationDraftView(fact.Id, value, fact.Quote, fact.PageRef, fact.Status, recorded, prefill, ComposedOf(prefill), matches));
    }

    public static async Task<MedicationFromFactResult> RecordAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, Guid factId, MedicationFromFactRequest request, DateOnly today,
        DateTimeOffset now, CancellationToken ct)
    {
        var errors = request.Validate(today);
        if (errors.Count > 0)
        {
            return new MedicationFromFactResult(MedicationFactOutcome.Invalid, null, errors);
        }

        var result = new MedicationFromFactResult(MedicationFactOutcome.NotFound);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            result = new MedicationFromFactResult(MedicationFactOutcome.NotFound);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // The document row is locked first, as the document delete does: a delete at the same time either
            // ran before (the document is gone here) or runs after (and clears this record's source fact).
            await FactService.LockDocumentAsync(db, documentId, ct);
            var document = await db.ExternalDocuments.AsNoTracking()
                .Where(d => d.Id == documentId && d.ProviderId == providerId && d.DeletedAt == null)
                .Select(d => new { d.PatientId })
                .SingleOrDefaultAsync(ct);
            var fact = document is null
                ? null
                : await db.ExtractedFacts.AsNoTracking()
                    .SingleOrDefaultAsync(f => f.Id == factId && f.ProviderId == providerId && f.ExternalDocumentId == documentId, ct);
            if (document is null || fact is null)
            {
                return;
            }
            var state = await ExternalDocumentService.PatientStateAsync(db, providerId, document.PatientId, ct);
            if (state != ExternalDocumentOutcome.Ok)
            {
                result = new MedicationFromFactResult(state == ExternalDocumentOutcome.Archived ? MedicationFactOutcome.Archived : MedicationFactOutcome.NotFound);
                return;
            }
            if (fact.Kind != FactKinds.Medication)
            {
                result = new MedicationFromFactResult(MedicationFactOutcome.NotMedication);
                return;
            }
            if (fact.Status == FactStatus.Discarded)
            {
                result = new MedicationFromFactResult(MedicationFactOutcome.NotPending);
                return;
            }

            // A stop closes the target and links nothing to the fact (it has no record of its own): the fact is
            // not marked recorded, and the same fact may be applied again (to another record, or as a new one).
            var stopping = request.Action == MedicationFromFactRequest.Stop;
            if (!stopping && await db.MedicationEntries.AnyAsync(m => m.SourceFactId == factId, ct))
            {
                result = new MedicationFromFactResult(MedicationFactOutcome.AlreadyRecorded);
                return;
            }

            MedicationEntry? target = null;
            if (request.Action != MedicationFromFactRequest.New)
            {
                target = await db.MedicationEntries.AsNoTracking()
                    .SingleOrDefaultAsync(m => m.Id == request.TargetMedicationId && m.PatientId == document.PatientId, ct);
                if (target is null)
                {
                    return;
                }
                if (target.EndedOn is not null)
                {
                    result = new MedicationFromFactResult(MedicationFactOutcome.TargetNotActive);
                    return;
                }
            }

            var starts = request.StartedOn;
            var ends = stopping ? request.EndedOn ?? request.StartedOn : request.EndedOn;
            if (target is not null && (stopping ? ends : starts) is { } date && date < target.StartedOn)
            {
                result = new MedicationFromFactResult(MedicationFactOutcome.Invalid, null, new Dictionary<string, string[]>
                {
                    [stopping ? "endedOn" : "startedOn"] = ["A data não pode ser anterior ao início do registro escolhido."],
                });
                return;
            }

            string? composed = null;
            if (!stopping)
            {
                composed = FactValidator.Normalize(FactKinds.Medication, Compose(request.Drug, request.Dose, request.Posology));
                if (composed is null)
                {
                    result = new MedicationFromFactResult(MedicationFactOutcome.Invalid, null, new Dictionary<string, string[]>
                    {
                        ["drug"] = ["Informe o medicamento."],
                    });
                    return;
                }
            }

            Guid medicationId;
            if (stopping)
            {
                if (!await CloseAsync(db, target!.Id, ends, ct))
                {
                    result = new MedicationFromFactResult(MedicationFactOutcome.TargetNotActive);
                    return;
                }
                medicationId = target.Id;
            }
            else
            {
                if (target is not null)
                {
                    // The old dose runs to the day before the new one (the same day when both start together).
                    var closeOn = starts!.Value > target.StartedOn ? starts.Value.AddDays(-1) : starts.Value;
                    if (!await CloseAsync(db, target.Id, closeOn, ct))
                    {
                        result = new MedicationFromFactResult(MedicationFactOutcome.TargetNotActive);
                        return;
                    }
                }
                medicationId = Guid.NewGuid();
                db.MedicationEntries.Add(new MedicationEntry
                {
                    Id = medicationId,
                    TreatmentId = target?.TreatmentId ?? medicationId,
                    PatientId = document.PatientId,
                    Drug = ProfileText.Clean(request.Drug)!,
                    Dose = ProfileText.Clean(request.Dose),
                    Posology = ProfileText.Clean(request.Posology),
                    StartedOn = starts!.Value,
                    EndedOn = ends,
                    SourceFactId = factId,
                    FromDocument = true,
                    CreatedAt = now,
                });
            }

            if (await ReviewFactAsync(db, providerId, fact, composed, now, ct) is { } factAction)
            {
                db.AuditEntries.Add(Audit(providerId, factAction, "extracted_fact", $"{fact.Id} {fact.Kind}", now));
            }
            db.AuditEntries.Add(Audit(providerId, AuditAction, AuditField, $"{fact.Id} {request.Action} {medicationId}", now));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            result = new MedicationFromFactResult(MedicationFactOutcome.Ok, medicationId);
        });
        return result;
    }

    private static async Task<bool> CloseAsync(AnamnysDbContext db, Guid medicationId, DateOnly? endedOn, CancellationToken ct) =>
        await db.MedicationEntries
            .Where(m => m.Id == medicationId && m.EndedOn == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.EndedOn, endedOn), ct) > 0;

    // Pending → confirmed, or corrected when the form's text differs from what was read; a reviewed fact is
    // corrected the same way (a suspension, which has no text, only confirms a pending one). Returns the fact
    // audit action, or null when nothing changed.
    private static async Task<string?> ReviewFactAsync(
        AnamnysDbContext db, Guid providerId, ExtractedFact fact, string? composed, DateTimeOffset now, CancellationToken ct)
    {
        if (composed is null && fact.Status != FactStatus.Pending)
        {
            return null;
        }
        // Saving the prefill unchanged confirms: formatting (case, commas, spacing) alone is not a correction.
        var same = composed is null || Equivalent(composed, fact.Value) || Equivalent(composed, ComposedOf(MedicationPrefill.From(fact.Value, fact.Quote, null, null)));
        var status = same ? FactStatus.Confirmed : FactStatus.Corrected;
        var correction = same ? null : composed;
        if (fact.Status == status && fact.CorrectedValue == correction)
        {
            return null;
        }
        await db.ExtractedFacts
            .Where(f => f.Id == fact.Id && f.ProviderId == providerId && f.Status != FactStatus.Discarded)
            .ExecuteUpdateAsync(s => s
                .SetProperty(f => f.Status, status)
                .SetProperty(f => f.CorrectedValue, correction)
                .SetProperty(f => f.ConfirmedBy, providerId)
                .SetProperty(f => f.ConfirmedAt, now), ct);
        return same ? FactService.ConfirmedAction : FactService.CorrectedAction;
    }

    private static AuditEntry Audit(Guid providerId, string action, string field, string newValue, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        ActorId = providerId,
        Action = action,
        FieldChanged = field,
        NewValue = newValue,
        At = now,
    };

    private static string? ComposedOf(MedicationDraft draft) =>
        draft.Drug is null ? null : Compose(draft.Drug, draft.Dose, draft.Posology);

    private static bool Equivalent(string? a, string? b) => a is not null && b is not null && Squash(a) == Squash(b);

    private static string Squash(string text) =>
        string.Concat(text.ToLowerInvariant().Where(char.IsLetterOrDigit));

    // The fact text the form stands for: "Sertralina 50 mg, 1 comprimido pela manhã".
    internal static string Compose(string? drug, string? dose, string? posology)
    {
        var head = string.Join(' ', new[] { ProfileText.Clean(drug), ProfileText.Clean(dose) }.OfType<string>());
        return ProfileText.Clean(posology) is { } p ? $"{head}, {p}" : head;
    }

    private static Task<bool> OwnsPatientAsync(AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct) =>
        db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId && p.ProviderId == providerId, ct);
}
