using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.Exports;

public enum ExportRequestOutcome { Ok, InProgress }

public sealed record ExportRequestResult(ExportRequestOutcome Outcome, Guid? Id, string? Status);

public sealed record ExportSummary(
    Guid Id, string Status, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt, long? SizeBytes, DateTimeOffset? ExpiresAt);

public enum ExportDownloadOutcome { Ok, NotFound, NotReady, Expired }

public sealed record ExportDownloadResult(ExportDownloadOutcome Outcome, Uri? Url, DateTimeOffset? ExpiresAt);

// design/specs/2026-10-23-account-export-design.md §2 and §5. Every query filters by the provider.
public static class AccountExportService
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Expired = "expired";

    public const string Requested = "account_export_requested";
    public const string Downloaded = "account_export_downloaded";
    public const string ReadyKind = "account_export_ready";
    public const string SubjectType = "account_export";

    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public static string ObjectKeyFor(Guid providerId, Guid exportId) => $"exports/{providerId}/{exportId}.zip";

    public static async Task<ExportRequestResult> RequestAsync(AnamnysDbContext db, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.AccountExports.AnyAsync(x => x.ProviderId == providerId && (x.Status == Queued || x.Status == Running), ct))
        {
            return new ExportRequestResult(ExportRequestOutcome.InProgress, null, null);
        }

        var export = new AccountExport { Id = Guid.NewGuid(), ProviderId = providerId, RequestedAt = now, Status = Queued };
        db.AccountExports.Add(export);
        AddAudit(db, providerId, Requested, now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return new ExportRequestResult(ExportRequestOutcome.InProgress, null, null);
        }
        return new ExportRequestResult(ExportRequestOutcome.Ok, export.Id, export.Status);
    }

    public static async Task<ExportSummary?> LatestAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct)
    {
        var export = await db.AccountExports.AsNoTracking()
            .Where(x => x.ProviderId == providerId)
            .OrderByDescending(x => x.RequestedAt).ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct);
        return export is null
            ? null
            : new ExportSummary(
                export.Id, export.Status, export.RequestedAt, export.CompletedAt, export.SizeBytes,
                export.CompletedAt + Retention);
    }

    public static async Task<ExportDownloadResult> DownloadAsync(
        AnamnysDbContext db, INoteFileStore store, Guid providerId, Guid exportId, DateTimeOffset now, CancellationToken ct)
    {
        var export = await db.AccountExports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == exportId && x.ProviderId == providerId, ct);
        if (export is null)
        {
            return new ExportDownloadResult(ExportDownloadOutcome.NotFound, null, null);
        }
        if (export.Status == Expired || (export.Status == Done && export.CompletedAt + Retention <= now))
        {
            return new ExportDownloadResult(ExportDownloadOutcome.Expired, null, null);
        }
        if (export.Status != Done || export.ObjectKey is null)
        {
            return new ExportDownloadResult(ExportDownloadOutcome.NotReady, null, null);
        }

        AddAudit(db, providerId, Downloaded, now);
        await db.SaveChangesAsync(ct);
        var fileName = $"anamnys-exportacao-{ExportFormat.IsoDate(export.CompletedAt!.Value)}.zip";
        var url = store.PresignGet(export.ObjectKey, fileName, LinkLifetime, "application/zip");
        return new ExportDownloadResult(ExportDownloadOutcome.Ok, url, now + LinkLifetime);
    }

    // Marks the export done and writes the bell notification in one transaction. False when the
    // export is no longer running (the caller then discards the uploaded object).
    internal static async Task<bool> CompleteAsync(
        AnamnysDbContext db, ExportJob job, string objectKey, long size, DateTimeOffset now, CancellationToken ct)
    {
        var completed = false;
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            completed = false;
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var updated = await db.AccountExports
                .Where(x => x.Id == job.ExportId && x.ProviderId == job.ProviderId && x.Status == Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, Done)
                    .SetProperty(x => x.CompletedAt, now)
                    .SetProperty(x => x.ObjectKey, objectKey)
                    .SetProperty(x => x.SizeBytes, size), ct);
            if (updated == 0)
            {
                return;
            }
            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                ProviderId = job.ProviderId,
                Kind = ReadyKind,
                SubjectType = SubjectType,
                SubjectId = job.ExportId,
                Channel = "in_app",
                ScheduledFor = now,
                SentAt = now,
                DeliveryStatus = "sent",
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            completed = true;
        });
        return completed;
    }

    public static Task<int> FailInterruptedAsync(AnamnysDbContext db, DateTimeOffset startedAt, CancellationToken ct) =>
        db.AccountExports
            .Where(x => (x.Status == Queued || x.Status == Running) && x.RequestedAt < startedAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, Failed)
                .SetProperty(x => x.FailureReason, "interrupted"), ct);

    // Deletes the object first: a failed delete leaves the row done so the next sweep retries.
    public static async Task<int> ExpireAsync(
        AnamnysDbContext db, INoteFileStore store, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        var cutoff = now - Retention;
        var due = await db.AccountExports.AsNoTracking()
            .Where(x => x.Status == Done && x.CompletedAt <= cutoff)
            .Select(x => new { x.Id, x.ProviderId, x.ObjectKey })
            .ToListAsync(ct);
        var expired = 0;
        foreach (var export in due)
        {
            try
            {
                if (export.ObjectKey is not null)
                {
                    await store.DeleteAsync(export.ObjectKey, ct);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogError("Could not delete the file of expired account export {ExportId}: {ExceptionType}", export.Id, e.GetType().Name);
                continue;
            }
            expired += await db.AccountExports
                .Where(x => x.Id == export.Id && x.ProviderId == export.ProviderId && x.Status == Done)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, Expired)
                    .SetProperty(x => x.ObjectKey, (string?)null)
                    .SetProperty(x => x.ExpiredAt, now), ct);
        }
        return expired;
    }

    private static void AddAudit(AnamnysDbContext db, Guid providerId, string action, DateTimeOffset at) =>
        db.AuditEntries.Add(new AuditEntry { Id = Guid.NewGuid(), ActorId = providerId, Action = action, At = at });
}
