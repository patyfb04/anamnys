using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Sharing;

// The provider's side of sharing (design/specs/2026-11-01-protected-sharing-design.md §3). Every query filters by the
// provider; another provider's document or link is NotFound, like a missing one. Audit values carry ids only.
public static class ShareService
{
    public const string AuthorizedAction = "disclosure_authorized";
    public const string LinkCreatedAction = "share_link_created";
    public const string LinkRevokedAction = "share_link_revoked";

    public const string TermOpenedAction = "disclosure_term_opened";

    public const int MaxWrongPasswords = 5;
    public const int MaxAccessesListed = 100;

    public static string TermKeyFor(Guid providerId, Guid authorizationId) => $"disclosure-terms/{providerId}/{authorizationId}";

    // Ok, NotFound, or why the document cannot get a new share.
    public static async Task<ShareOutcome> StateAsync(AnamnysDbContext db, Guid providerId, Guid documentId, CancellationToken ct)
    {
        var state = await (
            from d in db.ClinicalDocuments.AsNoTracking()
            join p in db.Patients.AsNoTracking() on d.PatientId equals p.Id
            where d.Id == documentId && d.ProviderId == providerId && p.ProviderId == providerId
            select new { d.SignedAt, d.SupersededBy, p.ArchivedAt }).SingleOrDefaultAsync(ct);
        return state switch
        {
            null => ShareOutcome.NotFound,
            { SignedAt: null } => ShareOutcome.NotSigned,
            { SupersededBy: not null } => ShareOutcome.Superseded,
            { ArchivedAt: not null } => ShareOutcome.Archived,
            _ => ShareOutcome.Ok,
        };
    }

    public static async Task<AuthorizationResult> RecordAuthorizationAsync(
        AnamnysDbContext db, INoteFileStore store, IVirusScanner scanner, Guid providerId, Guid documentId,
        RecordAuthorizationRequest request, TermUpload? term, DateTimeOffset now, CancellationToken ct)
    {
        var state = await StateAsync(db, providerId, documentId, ct);
        if (state != ShareOutcome.Ok)
        {
            return new AuthorizationResult(state);
        }
        string? mime = null;
        if (term is not null)
        {
            var check = await CheckTermAsync(scanner, term, ct);
            if (check.Outcome != ShareOutcome.Ok)
            {
                return new AuthorizationResult(check.Outcome);
            }
            mime = check.MimeType;
        }

        var id = Guid.NewGuid();
        var termKey = term is null ? null : TermKeyFor(providerId, id);
        var name = request.RecipientName!.Trim();
        var role = request.RecipientRole!.Trim();
        var purpose = request.Purpose!.Trim();
        try
        {
            return await db.InTransactionAsync(async c =>
            {
                await DocumentLocks.LockAsync(db, providerId, documentId, c);
                var current = await StateAsync(db, providerId, documentId, c);
                if (current != ShareOutcome.Ok)
                {
                    return new AuthorizationResult(current);
                }
                var existing = await db.DisclosureAuthorizations.AsNoTracking()
                    .Where(a => a.DocumentId == documentId && a.ProviderId == providerId
                        && a.RecipientName.ToLower() == name.ToLower()
                        && a.RecipientRole.ToLower() == role.ToLower()
                        && a.Purpose.ToLower() == purpose.ToLower())
                    .Select(a => (Guid?)a.Id)
                    .FirstOrDefaultAsync(c);
                if (existing is not null)
                {
                    return new AuthorizationResult(ShareOutcome.AuthorizationExists, existing);
                }

                if (term is not null)
                {
                    try
                    {
                        await store.PutAsync(termKey!, term.Content, mime!, c);
                    }
                    catch (Exception e) when (e is not OperationCanceledException || !c.IsCancellationRequested)
                    {
                        return new AuthorizationResult(ShareOutcome.StorageUnavailable);
                    }
                }
                var patientId = await db.ClinicalDocuments.Where(d => d.Id == documentId).Select(d => d.PatientId).SingleAsync(c);
                db.DisclosureAuthorizations.Add(new DisclosureAuthorization
                {
                    Id = id,
                    DocumentId = documentId,
                    PatientId = patientId,
                    ProviderId = providerId,
                    RecipientName = name,
                    RecipientRole = role,
                    Purpose = purpose,
                    AuthorizedAt = request.AuthorizedAt!.Value,
                    RecordedBy = providerId,
                    ObtainedVia = request.ObtainedVia!,
                    TermObjectKey = termKey,
                    ConfirmedAt = now,
                });
                DocumentAudit.Add(db, documentId, providerId, AuthorizedAction, "disclosure_authorization", null, id.ToString(), now);
                await db.SaveChangesAsync(c);
                return new AuthorizationResult(ShareOutcome.Ok, id);
            }, ct);
        }
        catch when (termKey is not null)
        {
            await ExternalDocumentService.DeleteBestEffortAsync(store, termKey);
            throw;
        }
    }

    public static async Task<LinkResult> CreateLinkAsync(
        AnamnysDbContext db, ShareAttemptLimiter limiter, Guid providerId, Guid documentId, CreateShareLinkRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var token = ShareSecrets.NewToken();
        var password = ShareSecrets.NewPassword();
        // Outside the transaction: the document stays locked for no longer than the writes need.
        string passwordHash;
        using (var slot = await limiter.AcquireAsync(ct))
        {
            if (!slot.IsAcquired)
            {
                return new LinkResult(ShareOutcome.Busy);
            }
            passwordHash = await ShareSecrets.HashPasswordAsync(password, ct);
        }
        var expiresAt = now.AddDays(request.ExpiresInDays ?? CreateShareLinkRequest.DefaultDays);
        var downloadEnabled = request.DownloadEnabled ?? false;
        var id = Guid.NewGuid();

        return await db.InTransactionAsync(async c =>
        {
            await DocumentLocks.LockAsync(db, providerId, documentId, c);
            var state = await StateAsync(db, providerId, documentId, c);
            if (state != ShareOutcome.Ok)
            {
                return new LinkResult(state);
            }
            var authorized = request.AuthorizationId is { } authorizationId && await db.DisclosureAuthorizations
                .AnyAsync(a => a.Id == authorizationId && a.DocumentId == documentId && a.ProviderId == providerId, c);
            if (!authorized)
            {
                return new LinkResult(ShareOutcome.AuthorizationRequired);
            }
            db.ShareLinks.Add(new ShareLink
            {
                Id = id,
                DocumentId = documentId,
                AuthorizationId = request.AuthorizationId!.Value,
                TokenHash = ShareSecrets.HashToken(token),
                PasswordHash = passwordHash,
                ExpiresAt = expiresAt,
                ViewCap = request.ViewCap,
                DownloadEnabled = downloadEnabled,
                CreatedAt = now,
            });
            DocumentAudit.Add(db, documentId, providerId, LinkCreatedAction, "share_link", null, $"{id} {request.AuthorizationId}", now);
            await db.SaveChangesAsync(c);
            return new LinkResult(ShareOutcome.Ok, new CreatedShareLink(id, token, password, expiresAt, request.ViewCap, downloadEnabled));
        }, ct);
    }

    // Takes effect at once: a password attempt holds the same row lock and reads RevokedAt after taking it. A second
    // revoke changes nothing and audits nothing.
    public static async Task<ShareOutcome> RevokeAsync(AnamnysDbContext db, Guid providerId, Guid linkId, DateTimeOffset now, CancellationToken ct) =>
        await db.InTransactionAsync(async c =>
        {
            await db.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM "ShareLinks" l JOIN "ClinicalDocuments" d ON d."Id" = l."DocumentId" WHERE l."Id" = {linkId} AND d."ProviderId" = {providerId} FOR UPDATE OF l""", c);
            var link = await (
                from l in db.ShareLinks
                join d in db.ClinicalDocuments on l.DocumentId equals d.Id
                where l.Id == linkId && d.ProviderId == providerId
                select l).SingleOrDefaultAsync(c);
            if (link is null)
            {
                return ShareOutcome.NotFound;
            }
            if (link.RevokedAt is null)
            {
                link.RevokedAt = now;
                DocumentAudit.Add(db, link.DocumentId, providerId, LinkRevokedAction, "share_link", null, link.Id.ToString(), now);
                await db.SaveChangesAsync(c);
            }
            return ShareOutcome.Ok;
        }, ct);

    public static async Task<DocumentShares?> ListAsync(AnamnysDbContext db, Guid providerId, Guid documentId, DateTimeOffset now, CancellationToken ct)
    {
        var document = await db.ClinicalDocuments.AsNoTracking()
            .Where(d => d.Id == documentId && d.ProviderId == providerId)
            .Select(d => new { d.SupersededBy })
            .SingleOrDefaultAsync(ct);
        if (document is null)
        {
            return null;
        }
        var state = await StateAsync(db, providerId, documentId, ct);
        var authorizations = await db.DisclosureAuthorizations.AsNoTracking()
            .Where(a => a.DocumentId == documentId && a.ProviderId == providerId)
            .OrderByDescending(a => a.ConfirmedAt)
            .ToListAsync(ct);
        var links = await db.ShareLinks.AsNoTracking()
            .Where(l => l.DocumentId == documentId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);
        var counts = await (
            from a in db.ShareAccesses.AsNoTracking()
            join l in db.ShareLinks.AsNoTracking() on a.ShareLinkId equals l.Id
            where l.DocumentId == documentId
            group a by a.ShareLinkId into g
            select new { LinkId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.LinkId, x => x.Count, ct);
        // The newest of each link; AccessCount tells the provider how many there are in all.
        var accesses = (await db.ShareLinks.AsNoTracking()
            .Where(l => l.DocumentId == documentId)
            .SelectMany(l => db.ShareAccesses.AsNoTracking()
                .Where(a => a.ShareLinkId == l.Id)
                .OrderByDescending(a => a.At).ThenByDescending(a => a.Id)
                .Take(MaxAccessesListed))
            .ToListAsync(ct))
            .OrderByDescending(a => a.At)
            .ToList();

        var items = authorizations.Select(a => new AuthorizationItem(
            a.Id, a.RecipientName, a.RecipientRole, a.Purpose, a.AuthorizedAt, a.ObtainedVia, a.TermObjectKey is not null,
            links.Where(l => l.AuthorizationId == a.Id)
                .Select(l => new ShareLinkItem(
                    l.Id, StatusOf(l, now), l.CreatedAt, l.ExpiresAt, l.ViewCap, l.ViewsUsed, l.DownloadEnabled, l.RevokedAt, l.BlockedAt,
                    accesses.Where(x => x.ShareLinkId == l.Id).Select(x => new ShareAccessItem(x.At, x.Outcome, x.UserAgent)).ToList(),
                    counts.GetValueOrDefault(l.Id)))
                .ToList()))
            .ToList();
        return new DocumentShares(documentId, state == ShareOutcome.Ok, ReasonOf(state), document.SupersededBy, items);
    }

    // Readable while the record exists, also for an archived patient. The audit entry commits before the file leaves.
    public static async Task<TermFile> OpenTermAsync(
        AnamnysDbContext db, INoteFileStore store, Guid providerId, Guid authorizationId, DateTimeOffset now, CancellationToken ct)
    {
        var authorization = await db.DisclosureAuthorizations.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == authorizationId && a.ProviderId == providerId, ct);
        if (authorization?.TermObjectKey is not { } key)
        {
            return new TermFile(ShareOutcome.NotFound);
        }
        byte[]? content;
        try
        {
            content = await store.GetAsync(key, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new TermFile(ShareOutcome.StorageUnavailable);
        }
        if (content is null)
        {
            return new TermFile(ShareOutcome.NotFound);
        }
        DocumentAudit.Add(db, authorization.DocumentId, providerId, TermOpenedAction, "disclosure_authorization", null, authorization.Id.ToString(), now);
        await db.SaveChangesAsync(ct);
        return new TermFile(ShareOutcome.Ok, content, FileTypeSniffer.MimeTypeOf(content) ?? "application/octet-stream");
    }

    // Why a link no longer opens, as the ShareAccesses outcome; null while it is live.
    internal static string? DeniedOutcome(ShareLink link, DateTimeOffset now) =>
        link.RevokedAt is not null ? ShareAccess.Revoked
        : link.BlockedAt is not null ? ShareAccess.Blocked
        : now >= link.ExpiresAt ? ShareAccess.Expired
        : link.ViewCap is { } cap && link.ViewsUsed >= cap ? ShareAccess.CapReached
        : null;

    private static string StatusOf(ShareLink link, DateTimeOffset now) => DeniedOutcome(link, now) switch
    {
        null => "active",
        ShareAccess.CapReached => "capped",
        var denied => denied,
    };

    private static string? ReasonOf(ShareOutcome state) => state switch
    {
        ShareOutcome.NotSigned => "not_signed",
        ShareOutcome.Superseded => "document_superseded",
        ShareOutcome.Archived => "archived",
        _ => null,
    };

    private static async Task<(ShareOutcome Outcome, string? MimeType)> CheckTermAsync(IVirusScanner scanner, TermUpload term, CancellationToken ct)
    {
        if (term.Content.LongLength > ExternalDocumentService.MaxBytes)
        {
            return (ShareOutcome.TermTooLarge, null);
        }
        var sniff = FileTypeSniffer.Sniff(term.FileName, term.Content);
        if (sniff.Outcome != SniffOutcome.Ok)
        {
            return (sniff.Outcome switch
            {
                SniffOutcome.Empty => ShareOutcome.TermEmpty,
                SniffOutcome.TypeMismatch => ShareOutcome.TermTypeMismatch,
                _ => ShareOutcome.TermUnsupportedType,
            }, null);
        }
        // Scanned before it is stored, in the request: nothing unscanned ever reaches the bucket, and a scanner that
        // cannot answer refuses the file rather than letting it through.
        try
        {
            var verdict = await scanner.ScanAsync(term.Content, ct);
            return verdict.Clean ? (ShareOutcome.Ok, sniff.MimeType) : (ShareOutcome.TermInfected, null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (ShareOutcome.ScannerUnavailable, null);
        }
    }
}
