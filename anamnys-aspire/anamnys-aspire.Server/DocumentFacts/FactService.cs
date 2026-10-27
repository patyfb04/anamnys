using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.DocumentReading;
using Anamnys.Server.Excerpts;
using Anamnys.Server.ExternalDocuments;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.DocumentFacts;

public enum FactOutcome { Ok, NotFound, Archived, NotPending, InvalidValue, NotAvailable }

public sealed record FactItem(
    Guid Id, string Kind, string Value, string? CorrectedValue, int? Page, string? Quote, string Status,
    Guid? ExcerptRefId, DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt, bool Recorded = false);

public sealed record DocumentFactsView(
    Guid DocumentId, string? ReadStatus, int? PageCount, bool PagesTruncated, bool Extracting, IReadOnlyList<FactItem> Facts);

// A fact that counts (confirmed or corrected): Value is what the record uses (the correction when there is
// one), ReadValue what the document said.
public sealed record DeclaredFact(
    Guid Id, Guid DocumentId, string DocumentKind, DateOnly? DocumentDate, DateTimeOffset UploadedAt, string Kind,
    string Value, string ReadValue, bool Corrected, int? Page, string? Quote, Guid? ExcerptRefId, DateTimeOffset? ConfirmedAt);

public sealed record ExtractRequestResult(FactOutcome Outcome, string? ReadStatus = null);

// design/specs/2026-10-26-document-facts-design.md §1 and §3. Every query filters by the provider and the
// document (not deleted) and its patient; another provider's document or fact is NotFound, like a missing
// one. Audit values carry the fact kind and id (or the document id and a count), never a fact value or
// page text.
public static class FactService
{
    public const string ConfirmedAction = "fact_confirmed";
    public const string CorrectedAction = "fact_corrected";
    public const string DiscardedAction = "fact_discarded";
    public const string ExtractedAction = "facts_extracted";
    public const string DeletedAction = "facts_deleted";
    private const string AuditField = "extracted_fact";

    // With the document's soft delete, in the caller's transaction (which has already updated, and so
    // locked, the document row): every fact of the document, whatever its status, and the document's
    // excerpt references go. Audited with the document id and the count, when there was any.
    public static async Task<int> DeleteForDocumentAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, DateTimeOffset now, CancellationToken ct)
    {
        var deleted = await db.ExtractedFacts
            .Where(f => f.ExternalDocumentId == documentId && f.ProviderId == providerId)
            .ExecuteDeleteAsync(ct);
        await db.ExcerptRefs
            .Where(r => r.SourceType == "document" && r.SourceId == documentId && r.ProviderId == providerId)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
        {
            db.AuditEntries.Add(new AuditEntry
            {
                Id = Guid.NewGuid(),
                ActorId = providerId,
                Action = DeletedAction,
                FieldChanged = AuditField,
                NewValue = $"{documentId} {deleted}",
                At = now,
            });
        }
        return deleted;
    }

    // Documents read but whose facts were not extracted after that read (a restart lost the queued or
    // running extraction): done, clean, not deleted, and no facts_extracted entry since ReadAt. Those whose
    // extraction was started maxAttempts times since the read are left out (ExhaustedCountAsync).
    public static async Task<IReadOnlyList<FactJob>> InterruptedAsync(AnamnysDbContext db, int maxAttempts, CancellationToken ct) =>
        await NotExtracted(db)
            .Where(d => d.ExtractAttempts < maxAttempts)
            .OrderBy(d => d.ReadAt)
            .Select(d => new FactJob(d.Id, d.ProviderId))
            .ToListAsync(ct);

    // Documents not extracted since their read whose extraction was started maxAttempts times: every one was
    // interrupted, so the sweep gives up on them (their pending facts stay; "Ler de novo" resets).
    public static Task<int> ExhaustedCountAsync(AnamnysDbContext db, int maxAttempts, CancellationToken ct) =>
        NotExtracted(db).CountAsync(d => d.ExtractAttempts >= maxAttempts, ct);

    // Counts one extraction attempt, before the model runs (a crash during it still counts).
    public static Task<int> CountExtractionAttemptAsync(AnamnysDbContext db, Guid providerId, Guid documentId, CancellationToken ct) =>
        db.ExternalDocuments
            .Where(d => d.Id == documentId && d.ProviderId == providerId && d.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ExtractAttempts, d => d.ExtractAttempts + 1), ct);

    private static IQueryable<ExternalDocument> NotExtracted(AnamnysDbContext db) =>
        db.ExternalDocuments.AsNoTracking()
            .Where(d => d.ReadStatus == DocumentReadStatus.Done && d.ScanStatus == ExternalDocumentService.Clean
                && d.DeletedAt == null && d.ReadAt != null
                && !db.AuditEntries.Any(a => a.Action == ExtractedAction && a.ActorId == d.ProviderId
                    && a.At >= d.ReadAt && a.NewValue!.StartsWith(d.Id.ToString())));

    internal static Task LockDocumentAsync(AnamnysDbContext db, Guid documentId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"ExternalDocuments\" WHERE \"Id\" = {documentId} FOR UPDATE", ct);

    // The pages of a document whose read is done, for the extractor; null when the document is not there.
    public static async Task<IReadOnlyList<FactPage>?> PagesForExtractionAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, CancellationToken ct)
    {
        var ready = await db.ExternalDocuments.AsNoTracking().AnyAsync(d => d.Id == documentId && d.ProviderId == providerId
            && d.DeletedAt == null && d.ScanStatus == ExternalDocumentService.Clean && d.ReadStatus == DocumentReadStatus.Done, ct);
        if (!ready)
        {
            return null;
        }
        return await db.ExternalDocumentPages.AsNoTracking()
            .Where(p => p.ExternalDocumentId == documentId && p.ProviderId == providerId)
            .OrderBy(p => p.PageNumber)
            .Select(p => new FactPage(p.PageNumber, p.Text))
            .ToListAsync(ct);
    }

    // Replaces the document's facts still awaiting review with the candidates that pass: a valid value
    // (FactValidator) and a quote found on the page the model named. Reviewed facts (confirmed, corrected,
    // discarded) stay, and a candidate repeating one of them (same kind and value) is not added again.
    // Returns how many facts were saved, or null when the document is gone or no longer read.
    public static async Task<int?> SaveExtractedAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, IReadOnlyList<FactCandidate> candidates, DateTimeOffset now,
        CancellationToken ct)
    {
        int? saved = null;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            saved = null;
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // The document row is locked first, as the delete does: a delete at the same time either runs
            // before (and this sees DeletedAt) or after (and removes these facts too).
            await LockDocumentAsync(db, documentId, ct);
            var document = await db.ExternalDocuments.AsNoTracking()
                .Where(d => d.Id == documentId && d.ProviderId == providerId && d.DeletedAt == null
                    && d.ScanStatus == ExternalDocumentService.Clean && d.ReadStatus == DocumentReadStatus.Done)
                .Select(d => new { d.DocumentDate, d.UploadedAt })
                .SingleOrDefaultAsync(ct);
            if (document is null)
            {
                return;
            }
            var pages = await db.ExternalDocumentPages.AsNoTracking()
                .Where(p => p.ExternalDocumentId == documentId && p.ProviderId == providerId)
                .ToDictionaryAsync(p => p.PageNumber, p => p.Text, ct);

            var existing = await db.ExtractedFacts.AsNoTracking()
                .Where(f => f.ExternalDocumentId == documentId && f.ProviderId == providerId)
                .Select(f => new { f.Id, f.Kind, f.Value, f.CorrectedValue, f.Status, f.ExcerptRefId })
                .ToListAsync(ct);
            var stale = existing.Where(f => f.Status == FactStatus.Pending).ToList();
            var seen = existing.Where(f => f.Status != FactStatus.Pending)
                .SelectMany(f => new[] { Key(f.Kind, f.Value), f.CorrectedValue is { } c ? Key(f.Kind, c) : null })
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            var staleIds = stale.Select(f => f.Id).ToList();
            var staleRefs = stale.Select(f => f.ExcerptRefId).OfType<Guid>().ToList();
            await db.ExtractedFacts
                .Where(f => staleIds.Contains(f.Id) && f.Status == FactStatus.Pending)
                .ExecuteDeleteAsync(ct);
            await db.ExcerptRefs
                .Where(r => staleRefs.Contains(r.Id) && r.ProviderId == providerId && r.SourceType == "document")
                .ExecuteDeleteAsync(ct);

            var datedAt = document.DocumentDate is { } date
                ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : document.UploadedAt;
            var count = 0;
            foreach (var candidate in FactValidator.WithCitedIcd(candidates))
            {
                if (FactValidator.Normalize(candidate.Kind, candidate.Value) is not { } value
                    || !pages.TryGetValue(candidate.Page, out var text)
                    || ExcerptService.FindInPage(text, candidate.Quote) is not { } span
                    || !seen.Add(Key(candidate.Kind, value)))
                {
                    continue;
                }
                var reference = ExcerptService.AddDocumentPage(db, providerId, documentId, candidate.Page, text, span, datedAt);
                db.ExtractedFacts.Add(new ExtractedFact
                {
                    Id = Guid.NewGuid(),
                    ExternalDocumentId = documentId,
                    ProviderId = providerId,
                    Kind = candidate.Kind,
                    Value = value,
                    PageRef = candidate.Page,
                    ExcerptRefId = reference.Id,
                    Status = FactStatus.Pending,
                    Quote = reference.Quote,
                    CreatedAt = now,
                });
                count++;
            }
            db.AuditEntries.Add(new AuditEntry
            {
                Id = Guid.NewGuid(),
                ActorId = providerId,
                Action = ExtractedAction,
                FieldChanged = AuditField,
                NewValue = $"{documentId} {count}",
                At = now,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            saved = count;
        });
        return saved;
    }

    // The document's facts for review (discarded ones are left out), grouped by kind in a fixed order.
    public static async Task<DocumentFactsView?> ListAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, bool extracting, CancellationToken ct)
    {
        var document = await FindDocument(db, providerId, documentId)
            .Select(d => new { d.ReadStatus, d.PageCount, d.PagesTruncated })
            .SingleOrDefaultAsync(ct);
        if (document is null)
        {
            return null;
        }
        var facts = await db.ExtractedFacts.AsNoTracking()
            .Where(f => f.ExternalDocumentId == documentId && f.ProviderId == providerId && f.Status != FactStatus.Discarded)
            .Select(f => new FactItem(f.Id, f.Kind, f.Value, f.CorrectedValue, f.PageRef, f.Quote, f.Status, f.ExcerptRefId, f.CreatedAt, f.ConfirmedAt,
                db.MedicationEntries.Any(m => m.SourceFactId == f.Id)))
            .ToListAsync(ct);
        return new DocumentFactsView(
            documentId, document.ReadStatus, document.PageCount, document.PagesTruncated, extracting,
            facts.OrderBy(f => Array.IndexOf(FactKinds.All, f.Kind)).ThenBy(f => f.Page).ThenBy(f => f.CreatedAt).ThenBy(f => f.Id).ToList());
    }

    // Facts awaiting review per document, for the document list and search.
    public static async Task<Dictionary<Guid, int>> PendingCountsAsync(
        AnamnysDbContext db, Guid providerId, IReadOnlyCollection<Guid> documentIds, CancellationToken ct)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }
        return await db.ExtractedFacts.AsNoTracking()
            .Where(f => f.ProviderId == providerId && documentIds.Contains(f.ExternalDocumentId) && f.Status == FactStatus.Pending)
            .GroupBy(f => f.ExternalDocumentId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
    }

    // Confirmed and corrected facts of the patient's documents (not deleted), newest document first; only
    // facts that point back to their sentence (ExcerptQueries.RequireExcerpt). Null for another provider's
    // patient or a missing one.
    public static async Task<IReadOnlyList<DeclaredFact>?> DeclaredForPatientAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct)
    {
        if (!await db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId && p.ProviderId == providerId, ct))
        {
            return null;
        }
        var rows = await (
            from f in db.ExtractedFacts.AsNoTracking().RequireExcerpt(f => f.ExcerptRefId)
            join d in db.ExternalDocuments.AsNoTracking() on f.ExternalDocumentId equals d.Id
            where f.ProviderId == providerId && d.ProviderId == providerId && d.PatientId == patientId && d.DeletedAt == null
                  && (f.Status == FactStatus.Confirmed || f.Status == FactStatus.Corrected)
            select new { f, d.Kind, d.DocumentDate, d.UploadedAt })
            .ToListAsync(ct);
        return rows
            .OrderByDescending(r => r.DocumentDate ?? DateOnly.FromDateTime(r.UploadedAt.UtcDateTime))
            .ThenByDescending(r => r.UploadedAt)
            .ThenBy(r => Array.IndexOf(FactKinds.All, r.f.Kind))
            .ThenBy(r => r.f.PageRef)
            .ThenBy(r => r.f.Id)
            .Select(r => new DeclaredFact(
                r.f.Id, r.f.ExternalDocumentId, r.Kind, r.DocumentDate, r.UploadedAt, r.f.Kind,
                r.f.Status == FactStatus.Corrected && r.f.CorrectedValue is { } c ? c : r.f.Value, r.f.Value,
                r.f.Status == FactStatus.Corrected, r.f.PageRef, r.f.Quote, r.f.ExcerptRefId, r.f.ConfirmedAt))
            .ToList();
    }

    // pending → confirmed. Confirming a confirmed fact is a no-op (Ok, no audit); a corrected or discarded
    // one is NotPending.
    public static Task<FactOutcome> ConfirmAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, Guid factId, DateTimeOffset now, CancellationToken ct) =>
        ReviewAsync(db, providerId, documentId, factId, async fact =>
        {
            if (fact.Status == FactStatus.Confirmed)
            {
                return (FactOutcome.Ok, null);
            }
            if (fact.Status != FactStatus.Pending)
            {
                return (FactOutcome.NotPending, null);
            }
            var updated = await Fact(db, providerId, factId).Where(f => f.Status == FactStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.Status, FactStatus.Confirmed)
                    .SetProperty(f => f.ConfirmedBy, providerId)
                    .SetProperty(f => f.ConfirmedAt, now), ct);
            return updated == 0 ? (await UnchangedAsync(db, providerId, factId, FactOutcome.NotPending, ct), null) : (FactOutcome.Ok, ConfirmedAction);
        }, now, ct);

    // pending, confirmed or corrected → corrected, the correction stored next to what was read. A
    // correction equal to what was read confirms the fact instead. The value follows the kind's format.
    public static Task<FactOutcome> CorrectAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, Guid factId, string? value, DateTimeOffset now, CancellationToken ct) =>
        ReviewAsync(db, providerId, documentId, factId, async fact =>
        {
            if (fact.Status == FactStatus.Discarded)
            {
                return (FactOutcome.NotPending, null);
            }
            if (FactValidator.Normalize(fact.Kind, value) is not { } corrected)
            {
                return (FactOutcome.InvalidValue, null);
            }
            var same = string.Equals(corrected, fact.Value, StringComparison.Ordinal);
            var status = same ? FactStatus.Confirmed : FactStatus.Corrected;
            var correction = same ? null : corrected;
            var updated = await Fact(db, providerId, factId).Where(f => f.Status != FactStatus.Discarded)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.Status, status)
                    .SetProperty(f => f.CorrectedValue, correction)
                    .SetProperty(f => f.ConfirmedBy, providerId)
                    .SetProperty(f => f.ConfirmedAt, now), ct);
            return updated == 0
                ? (await UnchangedAsync(db, providerId, factId, FactOutcome.NotPending, ct), null)
                : (FactOutcome.Ok, same ? ConfirmedAction : CorrectedAction);
        }, now, ct);

    // Any fact not yet discarded → discarded (soft: the row stays, out of review and out of the record).
    public static Task<FactOutcome> DiscardAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, Guid factId, DateTimeOffset now, CancellationToken ct) =>
        ReviewAsync(db, providerId, documentId, factId, async fact =>
        {
            if (fact.Status == FactStatus.Discarded)
            {
                return (FactOutcome.Ok, null);
            }
            var updated = await Fact(db, providerId, factId).Where(f => f.Status != FactStatus.Discarded)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.Status, FactStatus.Discarded)
                    .SetProperty(f => f.DiscardedAt, now), ct);
            return updated == 0 ? (await UnchangedAsync(db, providerId, factId, FactOutcome.Ok, ct), null) : (FactOutcome.Ok, DiscardedAction);
        }, now, ct);

    // "Ler de novo": a document whose read is done gets its facts extracted again (reviewed facts are kept);
    // one whose read failed (or never ran) is read again from scratch, attempts reset, and the read then
    // extracts. A read already pending or running is left alone. Only clean documents.
    public static async Task<ExtractRequestResult> RequestExtractionAsync(
        AnamnysDbContext db, ReadQueue reads, FactQueue facts, Guid providerId, Guid documentId, CancellationToken ct)
    {
        var document = await FindDocument(db, providerId, documentId)
            .Select(d => new { d.PatientId, d.ScanStatus, d.ReadStatus })
            .SingleOrDefaultAsync(ct);
        if (document is null)
        {
            return new ExtractRequestResult(FactOutcome.NotFound);
        }
        if (await ExternalDocumentService.PatientStateAsync(db, providerId, document.PatientId, ct) == ExternalDocumentOutcome.Archived)
        {
            return new ExtractRequestResult(FactOutcome.Archived);
        }
        if (document.ScanStatus != ExternalDocumentService.Clean)
        {
            return new ExtractRequestResult(FactOutcome.NotAvailable);
        }
        switch (document.ReadStatus)
        {
            case DocumentReadStatus.Done:
                // A new start for the sweep's attempt guard.
                await FindDocument(db, providerId, documentId)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.ExtractAttempts, 0), ct);
                facts.Enqueue(new FactJob(documentId, providerId));
                return new ExtractRequestResult(FactOutcome.Ok, DocumentReadStatus.Done);
            case DocumentReadStatus.Pending or DocumentReadStatus.Reading:
                return new ExtractRequestResult(FactOutcome.Ok, document.ReadStatus);
            default:
                var updated = await FindDocument(db, providerId, documentId)
                    .Where(d => d.ScanStatus == ExternalDocumentService.Clean
                        && (d.ReadStatus == null || d.ReadStatus == DocumentReadStatus.Failed))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.ReadStatus, DocumentReadStatus.Pending)
                        .SetProperty(d => d.ReadAttempts, 0)
                        .SetProperty(d => d.ExtractAttempts, 0), ct);
                if (updated > 0)
                {
                    reads.Enqueue(new ReadJob(documentId, providerId));
                }
                return new ExtractRequestResult(FactOutcome.Ok, DocumentReadStatus.Pending);
        }
    }

    // Shared shape of the review actions: the fact must be of the document, both the provider's, the
    // document not deleted and its patient not archived; the action and its audit entry commit together.
    private static async Task<FactOutcome> ReviewAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, Guid factId,
        Func<ExtractedFact, Task<(FactOutcome Outcome, string? AuditAction)>> apply, DateTimeOffset now, CancellationToken ct)
    {
        var outcome = FactOutcome.NotFound;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            var document = await FindDocument(db, providerId, documentId)
                .Select(d => new { d.PatientId })
                .SingleOrDefaultAsync(ct);
            var fact = document is null
                ? null
                : await Fact(db, providerId, factId).AsNoTracking()
                    .SingleOrDefaultAsync(f => f.ExternalDocumentId == documentId, ct);
            if (document is null || fact is null)
            {
                outcome = FactOutcome.NotFound;
                return;
            }
            var state = await ExternalDocumentService.PatientStateAsync(db, providerId, document.PatientId, ct);
            if (state != ExternalDocumentOutcome.Ok)
            {
                outcome = state == ExternalDocumentOutcome.Archived ? FactOutcome.Archived : FactOutcome.NotFound;
                return;
            }
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var (result, action) = await apply(fact);
            if (action is not null)
            {
                db.AuditEntries.Add(new AuditEntry
                {
                    Id = Guid.NewGuid(),
                    ActorId = providerId,
                    Action = action,
                    FieldChanged = AuditField,
                    NewValue = $"{fact.Id} {fact.Kind}",
                    At = now,
                });
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            outcome = result;
        });
        return outcome;
    }

    // A guarded update that changed nothing: the fact changed status in between (the given outcome), or a
    // document delete committed in between removed it (NotFound, like a fact that was never there).
    private static async Task<FactOutcome> UnchangedAsync(
        AnamnysDbContext db, Guid providerId, Guid factId, FactOutcome stillThere, CancellationToken ct) =>
        await Fact(db, providerId, factId).AnyAsync(ct) ? stillThere : FactOutcome.NotFound;

    private static IQueryable<ExternalDocument> FindDocument(AnamnysDbContext db, Guid providerId, Guid documentId) =>
        db.ExternalDocuments.AsNoTracking().Where(d => d.Id == documentId && d.ProviderId == providerId && d.DeletedAt == null);

    private static IQueryable<ExtractedFact> Fact(AnamnysDbContext db, Guid providerId, Guid factId) =>
        db.ExtractedFacts.Where(f => f.Id == factId && f.ProviderId == providerId);

    private static string Key(string kind, string value) => $"{kind}\u001f{value.Trim().ToLowerInvariant()}";
}
