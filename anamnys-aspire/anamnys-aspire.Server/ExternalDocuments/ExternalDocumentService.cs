using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.DocumentFacts;
using Anamnys.Server.Patients;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.ExternalDocuments;

// design/specs/2026-10-25-external-documents-design.md §3. Every query filters by the provider (and a
// document by its patient's provider); another provider's document is NotFound, like a missing one.
// No file name, patient name or content goes into object keys, logs or audit values.
public static class ExternalDocumentService
{
    public const long MaxBytes = 20L * 1024 * 1024;
    public const int PageSize = 25;
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public const string Pending = "pending";
    public const string Clean = "clean";
    public const string Infected = "infected";
    public const string Failed = "failed";

    public const string UploadedAction = "external_document_uploaded";
    public const string OpenedAction = "external_document_opened";
    public const string DeletedAction = "external_document_deleted";
    public const string BlockedAction = "external_document_blocked";

    private const string AuditField = "external_document";
    private const int MaxFileName = 200;

    public static string ObjectKeyFor(Guid providerId, Guid documentId) => $"external/{providerId}/{documentId}";

    // Ok, NotFound or Archived for the patient a document would belong to.
    public static async Task<ExternalDocumentOutcome> PatientStateAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct)
    {
        var archivedAt = await db.Patients.AsNoTracking()
            .Where(p => p.Id == patientId && p.ProviderId == providerId)
            .Select(p => new { p.ArchivedAt })
            .SingleOrDefaultAsync(ct);
        return archivedAt is null
            ? ExternalDocumentOutcome.NotFound
            : archivedAt.ArchivedAt is not null ? ExternalDocumentOutcome.Archived : ExternalDocumentOutcome.Ok;
    }

    public static async Task<ExternalDocumentUploadResult> UploadAsync(
        AnamnysDbContext db, INoteFileStore store, ScanQueue queue, Guid providerId, Guid patientId,
        ExternalDocumentUpload upload, DateTimeOffset now, CancellationToken ct)
    {
        var state = await PatientStateAsync(db, providerId, patientId, ct);
        if (state != ExternalDocumentOutcome.Ok)
        {
            return new ExternalDocumentUploadResult(state);
        }
        if (upload.Content.LongLength > MaxBytes)
        {
            return new ExternalDocumentUploadResult(ExternalDocumentOutcome.TooLarge);
        }
        var sniff = FileTypeSniffer.Sniff(upload.FileName, upload.Content);
        if (sniff.Outcome != SniffOutcome.Ok)
        {
            return new ExternalDocumentUploadResult(sniff.Outcome switch
            {
                SniffOutcome.Empty => ExternalDocumentOutcome.Empty,
                SniffOutcome.TypeMismatch => ExternalDocumentOutcome.TypeMismatch,
                _ => ExternalDocumentOutcome.UnsupportedType,
            });
        }

        var document = new ExternalDocument
        {
            Id = Guid.NewGuid(),
            PatientId = patientId,
            ProviderId = providerId,
            Filename = DisplayName(upload.FileName),
            MimeType = sniff.MimeType!,
            ByteSize = upload.Content.LongLength,
            ScanStatus = Pending,
            UploadedAt = now,
            Kind = upload.Kind,
            DocumentDate = upload.DocumentDate,
            Description = string.IsNullOrWhiteSpace(upload.Description) ? null : upload.Description.Trim(),
        };
        document.ObjectKey = ObjectKeyFor(providerId, document.Id);

        await store.PutAsync(document.ObjectKey, upload.Content, document.MimeType, ct);
        try
        {
            db.ExternalDocuments.Add(document);
            AddAudit(db, providerId, UploadedAction, document, now);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await DeleteBestEffortAsync(store, document.ObjectKey);
            throw;
        }
        queue.Enqueue(new ScanJob(document.Id, providerId));
        return new ExternalDocumentUploadResult(ExternalDocumentOutcome.Ok, document.Id);
    }

    public static async Task<IReadOnlyList<ExternalDocumentItem>?> ListAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct)
    {
        if (await PatientStateAsync(db, providerId, patientId, ct) == ExternalDocumentOutcome.NotFound)
        {
            return null;
        }
        var rows = await db.ExternalDocuments.AsNoTracking()
            .Where(d => d.ProviderId == providerId && d.PatientId == patientId && d.DeletedAt == null)
            .OrderByDescending(d => d.UploadedAt).ThenByDescending(d => d.Id)
            .ToListAsync(ct);
        var pending = await FactService.PendingCountsAsync(db, providerId, rows.Select(d => d.Id).ToList(), ct);
        return rows.Select(d => ToItem(d, null, pending.GetValueOrDefault(d.Id))).ToList();
    }

    public static async Task<ExternalDocumentSearchResponse> SearchAsync(
        AnamnysDbContext db, Guid providerId, ExternalDocumentSearchRequest request, CancellationToken ct)
    {
        var rows =
            from d in db.ExternalDocuments.AsNoTracking()
            join p in db.Patients.AsNoTracking() on d.PatientId equals p.Id
            where d.ProviderId == providerId && p.ProviderId == providerId && d.DeletedAt == null
            select new { d, p };
        if (request.Kind is { } kind)
        {
            rows = rows.Where(r => r.d.Kind == kind);
        }
        if (request.PatientId is { } patientId)
        {
            rows = rows.Where(r => r.d.PatientId == patientId);
        }
        if (PatientSearchQuery.ContainsPattern(request.Name) is { } name)
        {
            rows = rows.Where(r => EF.Functions.ILike(r.p.FirstName + " " + r.p.LastName, name, "\\"));
        }

        var page = request.Page ?? 1;
        var found = await rows
            .OrderByDescending(r => r.d.UploadedAt).ThenByDescending(r => r.d.Id)
            .Skip((page - 1) * PageSize)
            .Take(PageSize + 1)
            .Select(r => new { r.d, PatientName = r.p.FirstName + " " + r.p.LastName, Archived = r.p.ArchivedAt != null })
            .ToListAsync(ct);
        var shown = found.Take(PageSize).ToList();
        var pending = await FactService.PendingCountsAsync(db, providerId, shown.Select(r => r.d.Id).ToList(), ct);
        return new ExternalDocumentSearchResponse(
            shown.Select(r => ToItem(r.d, r.PatientName, pending.GetValueOrDefault(r.d.Id), r.Archived)).ToList(), found.Count > PageSize);
    }

    // Only a clean document opens; the audit entry is written before the link is handed out.
    public static async Task<ExternalDocumentOpenResult> OpenAsync(
        AnamnysDbContext db, INoteFileStore store, Guid providerId, Guid documentId, DateTimeOffset now, CancellationToken ct)
    {
        var document = await FindAsync(db, providerId, documentId, ct);
        if (document is null)
        {
            return new ExternalDocumentOpenResult(ExternalDocumentOutcome.NotFound);
        }
        if (document.ScanStatus != Clean || document.ObjectKey is null)
        {
            return new ExternalDocumentOpenResult(ExternalDocumentOutcome.NotAvailable);
        }

        AddAudit(db, providerId, OpenedAction, document, now);
        await db.SaveChangesAsync(ct);
        var url = store.PresignGetInline(document.ObjectKey, document.MimeType, LinkLifetime);
        return new ExternalDocumentOpenResult(ExternalDocumentOutcome.Ok, url, now + LinkLifetime);
    }

    // Soft delete: the row stays (DeletedAt), the object goes. The object is removed first so a failed
    // delete leaves the document as it was and the request can be repeated.
    public static async Task<ExternalDocumentOutcome> DeleteAsync(
        AnamnysDbContext db, INoteFileStore store, Guid providerId, Guid documentId, DateTimeOffset now, CancellationToken ct)
    {
        var document = await FindAsync(db, providerId, documentId, ct);
        if (document is null)
        {
            return ExternalDocumentOutcome.NotFound;
        }
        if (await PatientStateAsync(db, providerId, document.PatientId, ct) == ExternalDocumentOutcome.Archived)
        {
            return ExternalDocumentOutcome.Archived;
        }

        if (document.ObjectKey is { } key)
        {
            await store.DeleteAsync(key, ct);
        }
        // The row is updated (and so locked) before the pages read from the file are removed, in one
        // transaction: a read finishing at the same time waits for it and then sees the deletion (F-08).
        return await db.InTransactionAsync(async c =>
        {
            var updated = await db.ExternalDocuments
                .Where(d => d.Id == document.Id && d.ProviderId == providerId && d.DeletedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.ObjectKey, (string?)null)
                    .SetProperty(d => d.DeletedAt, now), c);
            if (updated == 0)
            {
                return ExternalDocumentOutcome.NotFound;
            }
            await db.ExternalDocumentPages
                .Where(p => p.ExternalDocumentId == document.Id && p.ProviderId == providerId)
                .ExecuteDeleteAsync(c);
            // So do the facts read from it, reviewed or not, and their excerpt references.
            await FactService.DeleteForDocumentAsync(db, providerId, document.Id, now, c);
            AddAudit(db, providerId, DeletedAction, document, now);
            await db.SaveChangesAsync(c);
            return ExternalDocumentOutcome.Ok;
        }, ct);
    }

    internal static void AddAudit(AnamnysDbContext db, Guid providerId, string action, ExternalDocument document, DateTimeOffset at) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            ActorId = providerId,
            Action = action,
            FieldChanged = AuditField,
            NewValue = $"{document.Id} {document.Kind}",
            At = at,
        });

    internal static async Task DeleteBestEffortAsync(INoteFileStore store, string key)
    {
        try
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
        catch
        {
            // The caller is already reporting the failure that led here.
        }
    }

    private static Task<ExternalDocument?> FindAsync(AnamnysDbContext db, Guid providerId, Guid documentId, CancellationToken ct) =>
        db.ExternalDocuments.SingleOrDefaultAsync(
            d => d.Id == documentId && d.ProviderId == providerId && d.DeletedAt == null, ct);

    private static ExternalDocumentItem ToItem(ExternalDocument d, string? patientName, int pendingFacts, bool? patientArchived = null) =>
        new(d.Id, d.PatientId, patientName, d.Filename, d.Kind, d.DocumentDate, d.Description, d.ScanStatus,
            d.ByteSize, d.UploadedAt, d.ScannedAt, patientArchived, d.ReadStatus, pendingFacts);

    // The name the browser sent, without any path or control characters; shown back, never used as a key.
    internal static string DisplayName(string raw)
    {
        var name = raw[(raw.LastIndexOfAny(['/', '\\']) + 1)..];
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > MaxFileName)
        {
            name = name[..MaxFileName];
        }
        return name.Length == 0 ? "documento" : name;
    }
}
