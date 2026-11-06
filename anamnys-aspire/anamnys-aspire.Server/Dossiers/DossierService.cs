using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Exports;
using Anamnys.Server.Retention;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.Dossiers;

public enum DossierOutcome { Ok, NotFound, InvalidReason, ReasonNoteRequired, InvalidPeriod, InProgress, NotReady, Expired }

public sealed record DossierRequestResult(DossierOutcome Outcome, Guid? Id = null, string? Status = null);

public sealed record DossierDownloadResult(DossierOutcome Outcome, Uri? Url = null, DateTimeOffset? ExpiresAt = null);

public sealed record DossierReceiptResult(DossierOutcome Outcome, byte[]? Pdf = null, string? FileName = null);

public sealed record DossierTokenResult(DossierOutcome Outcome, byte[]? Token = null, string? FileName = null);

// What the worker hands over when the ZIP is stored. GeneratedAt is the instant the dossier was built for (the one on its
// cover and in manifesto.json).
internal sealed record DossierCompletion(
    string ObjectKey, long SizeBytes, string ZipSha256, DossierManifest Manifest, byte[]? AnchorToken, DateTimeOffset? AnchoredAt,
    bool IntegrityVerified, DateTimeOffset GeneratedAt);

// design/specs/2026-11-04-patient-dossier-design.md §2, §3, §5. Every query filters by the provider. The reason note is
// stored on the row and shown on the cover and the receipt, never written to the audit or a log.
public static class DossierService
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Expired = "expired";

    public const string Other = "other";
    public static readonly IReadOnlyList<string> Reasons = ["crp", "court", "patient_request", Other];
    public const int MaxReasonNote = 200;

    public const string DossierError = "dossier_error";
    public const string Interrupted = "interrupted";

    public const string RequestedAction = "dossier_requested";
    public const string DownloadedAction = "dossier_downloaded";
    public const string ReceiptDownloadedAction = "dossier_receipt_downloaded";
    public const string AuditField = "dossier";
    public const string ReadyKind = "dossier_ready";
    public const string SubjectType = "dossier";

    public const int PageSize = 5;
    public const int MaxPage = 10_000;

    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    private const string ActiveKey = "Dossiers_Patient_active_key";
    private const int NumberAttempts = 5;

    public static string ObjectKeyFor(Guid providerId, Guid dossierId) => $"dossiers/{providerId}/{dossierId}.zip";

    public static string ZipFileName(int number, DateTimeOffset generatedAt) =>
        $"anamnys-dossie-{ExportFormat.IsoDate(generatedAt)}-{number:000}.zip";

    public static string ReceiptFileName(int number) => $"anamnys-dossie-{number:000}-comprovante.pdf";

    public static string TokenFileName(int number) => $"anamnys-dossie-{number:000}.tsr";

    // Null when the request is acceptable.
    public static DossierOutcome? Validate(DossierRequestBody? body)
    {
        if (body?.Reason is not { } reason || !Reasons.Contains(reason))
        {
            return DossierOutcome.InvalidReason;
        }
        if (reason == Other && (string.IsNullOrWhiteSpace(body.ReasonNote) || body.ReasonNote.Trim().Length > MaxReasonNote))
        {
            return DossierOutcome.ReasonNoteRequired;
        }
        if (body.PeriodFrom is { } from && body.PeriodTo is { } to && from > to)
        {
            return DossierOutcome.InvalidPeriod;
        }
        return null;
    }

    // An archived patient can have a dossier; a discarded one is gone.
    public static Task<bool> PatientExistsAsync(AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct) =>
        db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId && p.ProviderId == providerId && p.DiscardedAt == null, ct);

    public static async Task<DossierRequestResult> RequestAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DossierRequestBody? body, DateTimeOffset now, CancellationToken ct)
    {
        if (Validate(body) is { } invalid)
        {
            return new DossierRequestResult(invalid);
        }
        if (!await PatientExistsAsync(db, providerId, patientId, ct))
        {
            return new DossierRequestResult(DossierOutcome.NotFound);
        }
        var reason = body!.Reason!;
        // A note sent with another reason is dropped: the row keeps one only for "other".
        var note = reason == Other ? body.ReasonNote!.Trim() : null;

        for (var attempt = 1; ; attempt++)
        {
            if (await db.Dossiers.AsNoTracking().AnyAsync(
                    x => x.PatientId == patientId && x.ProviderId == providerId && (x.Status == Queued || x.Status == Running), ct))
            {
                return new DossierRequestResult(DossierOutcome.InProgress);
            }
            var number = (await db.Dossiers.Where(x => x.ProviderId == providerId).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
            var dossier = new Dossier
            {
                Id = Guid.NewGuid(),
                ProviderId = providerId,
                PatientId = patientId,
                Number = number,
                Status = Queued,
                Reason = reason,
                ReasonNote = note,
                PeriodFrom = body.PeriodFrom,
                PeriodTo = body.PeriodTo,
                RequestedAt = now,
            };
            db.Dossiers.Add(dossier);
            AddAudit(db, providerId, RequestedAction, $"{dossier.Id} {reason} {PeriodValue(body.PeriodFrom, body.PeriodTo)}", now);
            try
            {
                await db.SaveChangesAsync(ct);
                return new DossierRequestResult(DossierOutcome.Ok, dossier.Id, dossier.Status);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
            {
                db.ChangeTracker.Clear();
                if (pg.ConstraintName == ActiveKey)
                {
                    return new DossierRequestResult(DossierOutcome.InProgress);
                }
                // Another request of the same provider took this number: take the next one.
                if (attempt >= NumberAttempts)
                {
                    throw;
                }
            }
            catch (DbUpdateException e) when (DiscardedPatient.Is(e))
            {
                db.ChangeTracker.Clear();
                return new DossierRequestResult(DossierOutcome.NotFound);
            }
        }
    }

    // Null when the patient is not this provider's (or was discarded).
    public static async Task<DossierListPage?> ListAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, int page, DateTimeOffset now, CancellationToken ct)
    {
        if (!await PatientExistsAsync(db, providerId, patientId, ct))
        {
            return null;
        }
        page = Math.Clamp(page, 1, MaxPage);
        var query = db.Dossiers.AsNoTracking().Where(x => x.PatientId == patientId && x.ProviderId == providerId);
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.Number)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(x => new
            {
                x.Id, x.Number, x.Status, x.Reason, x.PeriodFrom, x.PeriodTo, x.RequestedAt, x.GeneratedAt, x.ExpiresAt, x.SizeBytes,
                Anchored = x.AnchorToken != null, x.IntegrityVerified,
            })
            .ToListAsync(ct);
        var items = rows
            .Select(x => new DossierListItem(
                x.Id, x.Number, EffectiveStatus(x.Status, x.ExpiresAt, now), x.Reason, x.PeriodFrom, x.PeriodTo, x.RequestedAt,
                x.GeneratedAt, x.ExpiresAt, x.SizeBytes, x.Anchored, x.GeneratedAt is null ? null : x.IntegrityVerified))
            .ToList();
        return new DossierListPage(items, total, page, PageSize);
    }

    public static async Task<DossierDownloadResult> DownloadAsync(
        AnamnysDbContext db, INoteFileStore store, Guid providerId, Guid dossierId, DateTimeOffset now, CancellationToken ct)
    {
        var dossier = await db.Dossiers.AsNoTracking()
            .Where(x => x.Id == dossierId && x.ProviderId == providerId)
            .Select(x => new { x.Number, x.Status, x.GeneratedAt, x.ExpiresAt, x.ObjectKey })
            .SingleOrDefaultAsync(ct);
        if (dossier is null)
        {
            return new DossierDownloadResult(DossierOutcome.NotFound);
        }
        if (EffectiveStatus(dossier.Status, dossier.ExpiresAt, now) == Expired)
        {
            return new DossierDownloadResult(DossierOutcome.Expired);
        }
        if (dossier.Status != Done || dossier.ObjectKey is null || dossier.GeneratedAt is null)
        {
            return new DossierDownloadResult(DossierOutcome.NotReady);
        }

        // The link never outlives the ZIP: one hour, or less when the dossier expires sooner.
        var linkExpiresAt = dossier.ExpiresAt is { } expiresAt && expiresAt < now + LinkLifetime ? expiresAt : now + LinkLifetime;
        var url = store.PresignGet(
            dossier.ObjectKey, ZipFileName(dossier.Number, dossier.GeneratedAt.Value), linkExpiresAt - now, "application/zip");
        AddAudit(db, providerId, DownloadedAction, dossierId.ToString(), now);
        await db.SaveChangesAsync(ct);
        return new DossierDownloadResult(DossierOutcome.Ok, url, linkExpiresAt);
    }

    // The RFC 3161 token over the ZIP's SHA-256 digest, raw DER, for `openssl ts -verify -data <zip> -in <tsr> -token_in`. NotFound
    // when unknown, another provider's, or generated without a token; NotReady before generation. Audited as a receipt
    // download (NewValue "<id> tsr").
    public static async Task<DossierTokenResult> TokenAsync(
        AnamnysDbContext db, Guid providerId, Guid dossierId, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.Dossiers.AsNoTracking()
            .Where(x => x.Id == dossierId && x.ProviderId == providerId)
            .Select(x => new { x.Number, x.Status, x.AnchorToken })
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return new DossierTokenResult(DossierOutcome.NotFound);
        }
        if (row.Status is not (Done or Expired))
        {
            return new DossierTokenResult(DossierOutcome.NotReady);
        }
        if (row.AnchorToken is null)
        {
            return new DossierTokenResult(DossierOutcome.NotFound);
        }
        AddAudit(db, providerId, ReceiptDownloadedAction, $"{dossierId} tsr", now);
        await db.SaveChangesAsync(ct);
        return new DossierTokenResult(DossierOutcome.Ok, row.AnchorToken, TokenFileName(row.Number));
    }

    // The delivery receipt (spec §5), for done and expired dossiers; audited before the bytes are returned.
    public static async Task<DossierReceiptResult> ReceiptAsync(
        AnamnysDbContext db, Guid providerId, Guid dossierId, DateTimeOffset now, CancellationToken ct)
    {
        var row = await (
                from d in db.Dossiers.AsNoTracking()
                join p in db.Patients.AsNoTracking() on d.PatientId equals p.Id
                join v in db.Providers.AsNoTracking() on d.ProviderId equals v.Id
                where d.Id == dossierId && d.ProviderId == providerId && p.ProviderId == providerId
                select new
                {
                    d.Number, d.Status, d.Reason, d.ReasonNote, d.PeriodFrom, d.PeriodTo, d.RequestedAt, d.GeneratedAt, d.ExpiresAt,
                    d.ExpiredAt, d.ZipSha256, d.Manifest, d.AnchorToken, d.AnchoredAt,
                    p.FirstName, p.LastName, ProviderName = v.Name, v.CrpNumber, v.CrpRegion,
                })
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return new DossierReceiptResult(DossierOutcome.NotFound);
        }
        if (row.Status is not (Done or Expired) || row.GeneratedAt is null || row.ZipSha256 is null || row.Manifest is null)
        {
            return new DossierReceiptResult(DossierOutcome.NotReady);
        }

        var manifest = DossierManifest.FromJson(row.Manifest);
        var status = EffectiveStatus(row.Status, row.ExpiresAt, now) == Expired
            ? $"expirado em {DossierReceiptPdf.Day(row.ExpiredAt ?? row.ExpiresAt ?? now)} (o arquivo ZIP foi excluído; este registro é mantido)"
            : $"pronto para download até {DossierReceiptPdf.Day(row.ExpiresAt!.Value)}";
        var pdf = DossierReceiptPdf.Render(new DossierReceiptModel(
            row.Number,
            row.ProviderName,
            row.CrpNumber is null ? "" : $"{row.CrpNumber}/{row.CrpRegion}",
            $"{row.FirstName} {row.LastName}".Trim(),
            row.RequestedAt,
            row.GeneratedAt.Value,
            DossierPdf.ReasonText(row.Reason, row.ReasonNote),
            DossierPdf.PeriodText(row.PeriodFrom, row.PeriodTo),
            row.ZipSha256,
            row.AnchorToken is null ? null : row.AnchoredAt,
            row.AnchorToken is null ? null : AuthorityOf(row.AnchorToken),
            manifest?.Files ?? [],
            status,
            now,
            ZipFileName(row.Number, row.GeneratedAt.Value),
            TokenFileName(row.Number)));

        AddAudit(db, providerId, ReceiptDownloadedAction, dossierId.ToString(), now);
        await db.SaveChangesAsync(ct);
        return new DossierReceiptResult(DossierOutcome.Ok, pdf, ReceiptFileName(row.Number));
    }

    // Marks the dossier done and writes the bell notice in one transaction. False when the dossier is no longer running
    // (the caller then discards the uploaded object).
    internal static Task<bool> CompleteAsync(
        AnamnysDbContext db, DossierJob job, DossierCompletion c, DateTimeOffset now, CancellationToken ct) =>
        db.InTransactionAsync(async ct =>
        {
            var manifest = c.Manifest.ToJson();
            var updated = await db.Dossiers
                .Where(x => x.Id == job.DossierId && x.ProviderId == job.ProviderId && x.Status == Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, Done)
                    .SetProperty(x => x.GeneratedAt, c.GeneratedAt)
                    .SetProperty(x => x.ExpiresAt, c.GeneratedAt + Retention)
                    .SetProperty(x => x.ObjectKey, c.ObjectKey)
                    .SetProperty(x => x.SizeBytes, c.SizeBytes)
                    .SetProperty(x => x.ZipSha256, c.ZipSha256)
                    .SetProperty(x => x.Manifest, manifest)
                    .SetProperty(x => x.AnchorToken, c.AnchorToken)
                    .SetProperty(x => x.AnchoredAt, c.AnchoredAt)
                    .SetProperty(x => x.IntegrityVerified, c.IntegrityVerified), ct);
            if (updated == 0)
            {
                return false;
            }
            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                ProviderId = job.ProviderId,
                Kind = ReadyKind,
                SubjectType = SubjectType,
                SubjectId = job.DossierId,
                Channel = "in_app",
                ScheduledFor = now,
                SentAt = now,
                DeliveryStatus = "sent",
            });
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    internal static Task<int> FailAsync(AnamnysDbContext db, DossierJob job, string reason, CancellationToken ct) =>
        db.Dossiers
            .Where(x => x.Id == job.DossierId && x.ProviderId == job.ProviderId && x.Status == Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, Failed)
                .SetProperty(x => x.FailureReason, reason), ct);

    // At startup: a dossier a previous process left running lost its build with it.
    public static Task<int> FailInterruptedAsync(AnamnysDbContext db, DateTimeOffset startedAt, CancellationToken ct) =>
        db.Dossiers
            .Where(x => x.Status == Running && x.RequestedAt < startedAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, Failed)
                .SetProperty(x => x.FailureReason, Interrupted), ct);

    // At startup: the queue is in memory, so the rows still queued by a previous process are enqueued again.
    public static Task<List<DossierJob>> QueuedBeforeAsync(AnamnysDbContext db, DateTimeOffset startedAt, CancellationToken ct) =>
        db.Dossiers.AsNoTracking()
            .Where(x => x.Status == Queued && x.RequestedAt < startedAt)
            .OrderBy(x => x.RequestedAt)
            .Select(x => new DossierJob(x.Id, x.ProviderId))
            .ToListAsync(ct);

    // Deletes the object first: a failed delete leaves the row done so the next sweep retries.
    public static async Task<int> ExpireAsync(
        AnamnysDbContext db, INoteFileStore store, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        var due = await db.Dossiers.AsNoTracking()
            .Where(x => x.Status == Done && x.ExpiresAt <= now)
            .Select(x => new { x.Id, x.ProviderId, x.ObjectKey })
            .ToListAsync(ct);
        var expired = 0;
        foreach (var dossier in due)
        {
            try
            {
                if (dossier.ObjectKey is not null)
                {
                    await store.DeleteAsync(dossier.ObjectKey, ct);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogError("Could not delete the file of expired dossier {DossierId}: {ExceptionType}", dossier.Id, e.GetType().Name);
                continue;
            }
            expired += await db.Dossiers
                .Where(x => x.Id == dossier.Id && x.ProviderId == dossier.ProviderId && x.Status == Done)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, Expired)
                    .SetProperty(x => x.ObjectKey, (string?)null)
                    .SetProperty(x => x.ExpiredAt, now), ct);
        }
        return expired;
    }

    // A done dossier past its lifetime is expired for the provider even before the sweep deletes its file.
    public static string EffectiveStatus(string status, DateTimeOffset? expiresAt, DateTimeOffset now) =>
        status == Done && expiresAt <= now ? Expired : status;

    // The common name of the certificate that signed the token, as the authority to show on the receipt.
    internal static string? AuthorityOf(byte[] token)
    {
        try
        {
            if (!Rfc3161TimestampToken.TryDecode(token, out var decoded, out _))
            {
                return null;
            }
            var signers = decoded.AsSignedCms().SignerInfos;
            var name = signers.Count > 0 ? signers[0].Certificate?.GetNameInfo(X509NameType.SimpleName, false) : null;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string PeriodValue(DateOnly? from, DateOnly? to) =>
        $"{from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}..{to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}";

    private static void AddAudit(AnamnysDbContext db, Guid providerId, string action, string value, DateTimeOffset at) =>
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            ActorId = providerId,
            Action = action,
            FieldChanged = AuditField,
            NewValue = value,
            At = at,
        });
}
