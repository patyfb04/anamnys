using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Excerpts;
using Anamnys.Server.Notes;
using Anamnys.Server.Profile;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Analysis;

public enum PlanOutcome { Ok, NotFound, Archived }

public sealed record PlanReviewResult(PlanOutcome Outcome, Guid? PlanId = null, DateTimeOffset? ReviewedAt = null);

// NoteNotReady: the note is not in review or signed (ReadyForReview, Signed, Exported).
public enum FollowUpOutcome { Ok, NotFound, Archived, InvalidSelection, InvalidNote, NoteNotReady }

public sealed record FollowUpResult(FollowUpOutcome Outcome, Guid? Id = null);

// "Marcar para retomar" (design/specs/2026-10-30-plan-adherence-design.md §3). The passage is optional: a selection
// (sectionKey + offsetStart/offsetEnd, the selected text becomes the quote), a section (sectionKey), or a quote to
// locate. Without any of them the follow-up keeps no passage. Body only: descriptions and quotes never go in a URL.
public sealed record FollowUpRequest(
    string? Description, string? SectionKey = null, int? OffsetStart = null, int? OffsetEnd = null, string? Quote = null)
{
    public const int MaxDescription = 500;

    public bool HasPassage => SectionKey is not null || OffsetStart is not null || OffsetEnd is not null || !string.IsNullOrWhiteSpace(Quote);

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        var description = ProfileText.Clean(Description);
        if (description is null)
        {
            errors["description"] = ["Descreva o ponto a retomar."];
        }
        else if (description.Length > MaxDescription)
        {
            errors["description"] = [$"Use no máximo {MaxDescription} caracteres."];
        }
        if ((OffsetStart is null) != (OffsetEnd is null) || (OffsetStart is not null && SectionKey is null))
        {
            errors["offsetStart"] = ["Informe a seção e o início e o fim do trecho."];
        }
        if (Quote is { Length: > ExcerptQueries.MaxQuoteLength })
        {
            errors["quote"] = [$"Use no máximo {ExcerptQueries.MaxQuoteLength} caracteres."];
        }
        return errors;
    }
}

public sealed record ResolveFollowUpRequest(Guid? NoteId = null);

// Plan review and follow-up writes. A record of another provider (or discarded, F-06) is NotFound; an archived one is
// read-only (Archived). Every change is audited with ids only, in the transaction that makes it; a repeated resolve or
// reopen is a no-op (no audit).
public static class PlanAdherenceService
{
    public const string PlanReviewed = "plan_reviewed";
    public const string FollowUpCreated = "follow_up_created";
    public const string FollowUpResolved = "follow_up_resolved";
    public const string FollowUpReopened = "follow_up_reopened";

    // Marks the open plan as reviewed now; a patient without one gets it here.
    public static async Task<PlanReviewResult> ReviewAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        var result = new PlanReviewResult(PlanOutcome.NotFound);
        await InTransactionAsync(db, async () =>
        {
            // FOR UPDATE: two reviews of a patient without a plan must not open two plans.
            var patient = await db.Database
                .SqlQuery<DateTimeOffset?>($"""
                    SELECT "ArchivedAt" AS "Value" FROM "Patients"
                     WHERE "Id" = {patientId} AND "ProviderId" = {providerId} AND "DiscardedAt" IS NULL FOR UPDATE
                    """)
                .ToListAsync(ct);
            if (patient.Count == 0)
            {
                result = new PlanReviewResult(PlanOutcome.NotFound);
                return false;
            }
            if (patient[0] is not null)
            {
                result = new PlanReviewResult(PlanOutcome.Archived);
                return false;
            }
            var plan = await db.TreatmentPlans
                .Where(t => t.PatientId == patientId && t.ClosedAt == null)
                .OrderByDescending(t => t.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (plan is null)
            {
                plan = new TreatmentPlan { Id = Guid.NewGuid(), PatientId = patientId, CreatedAt = nowUtc };
                db.TreatmentPlans.Add(plan);
            }
            plan.ReviewedAt = nowUtc;
            Audit(db, null, providerId, PlanReviewed, "plan", $"{patientId} {plan.Id}", nowUtc);
            await db.SaveChangesAsync(ct);
            result = new PlanReviewResult(PlanOutcome.Ok, plan.Id, nowUtc);
            return true;
        }, ct);
        return result;
    }

    public static async Task<FollowUpResult> CreateFollowUpAsync(
        AnamnysDbContext db, Guid providerId, Guid noteId, FollowUpRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        var result = new FollowUpResult(FollowUpOutcome.NotFound);
        await InTransactionAsync(db, async () =>
        {
            // The note lock keeps the passage valid against a concurrent section save (ExcerptService contract).
            await NoteLocks.LockAsync(db, noteId, ct);
            var note = await NoteAsync(db, providerId, noteId, ct);
            if (note is null)
            {
                result = new FollowUpResult(FollowUpOutcome.NotFound);
                return false;
            }
            if (note.Archived)
            {
                result = new FollowUpResult(FollowUpOutcome.Archived);
                return false;
            }
            if (!MarkableStatuses.Contains(note.Status))
            {
                result = new FollowUpResult(FollowUpOutcome.NoteNotReady);
                return false;
            }

            ExcerptRef? passage = null;
            if (request.HasPassage)
            {
                var hasSelection = request.OffsetStart is not null || request.OffsetEnd is not null;
                var quoted = !hasSelection && !string.IsNullOrWhiteSpace(request.Quote);
                var reference = await ExcerptService.AddAsync(
                    db, providerId, noteId, request.SectionKey, quoted ? request.Quote : null, request.OffsetStart, request.OffsetEnd, ct);
                // A quote that is not found in the text is refused: no silent fallback to a whole-section reference.
                if (reference.Outcome != ExcerptOutcome.Ok || (quoted && reference.Ref?.Kind != "exact"))
                {
                    result = new FollowUpResult(FollowUpOutcome.InvalidSelection);
                    return false;
                }
                passage = reference.Ref;
            }

            var item = new FollowUpItem
            {
                Id = Guid.NewGuid(),
                ProviderId = providerId,
                PatientId = note.PatientId,
                NoteId = noteId,
                ExcerptRefId = passage?.Id,
                Description = ProfileText.Clean(request.Description)!,
                CreatedAt = nowUtc,
            };
            db.FollowUpItems.Add(item);
            Audit(db, noteId, providerId, FollowUpCreated, "follow_up", item.Id.ToString(), nowUtc);
            await db.SaveChangesAsync(ct);
            result = new FollowUpResult(FollowUpOutcome.Ok, item.Id);
            return true;
        }, ct);
        return result;
    }

    // From a later note (noteId, a note of the same patient) or from the plan card (no note).
    public static async Task<FollowUpOutcome> ResolveFollowUpAsync(
        AnamnysDbContext db, Guid providerId, Guid id, Guid? noteId, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        var outcome = FollowUpOutcome.NotFound;
        await InTransactionAsync(db, async () =>
        {
            var (item, archived) = await ItemAsync(db, providerId, id, ct);
            if (item is null)
            {
                outcome = FollowUpOutcome.NotFound;
                return false;
            }
            if (archived)
            {
                outcome = FollowUpOutcome.Archived;
                return false;
            }
            if (item.ResolvedAt is not null)
            {
                outcome = FollowUpOutcome.Ok;
                return false;
            }
            // The resolving note is a later note of the same patient: not the one it was marked on, and not dated
            // (session date rule) before it.
            if (noteId is { } resolving)
            {
                var dated = await CaseAnalysisQuery.DatedNotesAsync(db, providerId, item.PatientId, signedOnly: false, ct);
                var target = dated.FirstOrDefault(n => n.NoteId == resolving);
                var origin = item.NoteId is { } originId ? dated.FirstOrDefault(n => n.NoteId == originId) : null;
                if (target is null || resolving == item.NoteId || (origin is not null && target.At < origin.At))
                {
                    outcome = FollowUpOutcome.InvalidNote;
                    return false;
                }
            }
            item.ResolvedAt = nowUtc;
            item.ResolvedNoteId = noteId;
            item.ResolvedById = providerId;
            Audit(db, noteId ?? item.NoteId, providerId, FollowUpResolved, "follow_up", item.Id.ToString(), nowUtc);
            await db.SaveChangesAsync(ct);
            outcome = FollowUpOutcome.Ok;
            return true;
        }, ct);
        return outcome;
    }

    public static async Task<FollowUpOutcome> ReopenFollowUpAsync(
        AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        var outcome = FollowUpOutcome.NotFound;
        await InTransactionAsync(db, async () =>
        {
            var (item, archived) = await ItemAsync(db, providerId, id, ct);
            if (item is null)
            {
                outcome = FollowUpOutcome.NotFound;
                return false;
            }
            if (archived)
            {
                outcome = FollowUpOutcome.Archived;
                return false;
            }
            if (item.ResolvedAt is null)
            {
                outcome = FollowUpOutcome.Ok;
                return false;
            }
            item.ResolvedAt = null;
            item.ResolvedNoteId = null;
            item.ResolvedById = null;
            Audit(db, item.NoteId, providerId, FollowUpReopened, "follow_up", item.Id.ToString(), nowUtc);
            await db.SaveChangesAsync(ct);
            outcome = FollowUpOutcome.Ok;
            return true;
        }, ct);
        return outcome;
    }

    private static readonly string[] MarkableStatuses = ["ReadyForReview", "Signed", "Exported"];

    private sealed record NoteRow(Guid PatientId, bool Archived, string Status);

    private static Task<NoteRow?> NoteAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct) =>
        (from n in db.Notes.AsNoTracking()
         join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
         where n.Id == noteId && n.ProviderId == providerId && p.ProviderId == providerId && p.DiscardedAt == null
         select new NoteRow(p.Id, p.ArchivedAt != null, n.Status))
        .SingleOrDefaultAsync(ct);

    // The item (tracked, row locked) when it belongs to the provider and its record is not discarded.
    private static async Task<(FollowUpItem? Item, bool Archived)> ItemAsync(AnamnysDbContext db, Guid providerId, Guid id, CancellationToken ct)
    {
        // Materialised before SingleOrDefault so EF does not wrap the locking query in a subquery.
        var item = (await db.FollowUpItems
            .FromSql($"""SELECT * FROM "FollowUpItems" WHERE "Id" = {id} AND "ProviderId" = {providerId} FOR UPDATE""")
            .ToListAsync(ct)).SingleOrDefault();
        if (item is null)
        {
            return (null, false);
        }
        var patient = await db.Patients.AsNoTracking()
            .Where(p => p.Id == item.PatientId && p.ProviderId == providerId && p.DiscardedAt == null)
            .Select(p => new { p.ArchivedAt })
            .SingleOrDefaultAsync(ct);
        return patient is null ? (null, false) : (item, patient.ArchivedAt is not null);
    }

    private static void Audit(AnamnysDbContext db, Guid? noteId, Guid providerId, string action, string field, string ids, DateTimeOffset at) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            NoteId = noteId,
            ActorId = providerId,
            Action = action,
            FieldChanged = field,
            NewValue = ids,
            At = at,
        });

    // Commits only when the body says so: refusals and no-ops leave no trace.
    private static async Task InTransactionAsync(AnamnysDbContext db, Func<Task<bool>> body, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await body())
            {
                await tx.CommitAsync(ct);
            }
        });
    }
}
