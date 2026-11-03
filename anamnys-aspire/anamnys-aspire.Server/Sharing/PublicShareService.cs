using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Sharing;

// The recipient's side (design/specs/2026-11-01-protected-sharing-design.md §3). A link is found only by the hash of its
// token. Unknown, expired, revoked, capped and blocked are one outcome, Unavailable, and each costs one Argon2 run.
public static class PublicShareService
{
    public const int MaxUserAgent = 512;

    // A dead link logs one attempt per link and outcome in this period: whoever holds the token cannot grow the table.
    public static readonly TimeSpan DeadLinkLogPeriod = TimeSpan.FromMinutes(1);

    public static async Task<bool> CheckAsync(AnamnysDbContext db, string token, DateTimeOffset now, CancellationToken ct)
    {
        var hash = ShareSecrets.HashToken(token);
        var link = await db.ShareLinks.AsNoTracking().SingleOrDefaultAsync(l => l.TokenHash == hash, ct);
        return link is not null && ShareService.DeniedOutcome(link, now) is null;
    }

    // The slot comes first: no connection, row lock or hash is spent without one, and with none free the answer is Busy.
    // A token with no live link hashes outside any transaction. A live link holds its row lock from the state check to the
    // commit, Argon2 included, so attempts on one link run one at a time: parallel guesses cannot pass the fifth wrong
    // password, and parallel opens cannot pass the view cap. The clock is read after the lock, so a wait cannot outlast the
    // expiry. The request's token applies until the password is checked: from there the attempt always counts, or dropping
    // the connection would undo a wrong password. The view is counted in the same transaction as the render, so a render
    // that fails uses no view and logs no opening. The PDF goes back in this response: nothing is stored and no link exists.
    public static async Task<PublicOpenResult> OpenAsync(
        AnamnysDbContext db, ShareAttemptLimiter limiter, TimeProvider clock, string token, string password, string? userAgent, CancellationToken ct)
    {
        using var slot = await limiter.AcquireAsync(ct);
        if (!slot.IsAcquired)
        {
            return new PublicOpenResult(PublicOpenOutcome.Busy);
        }

        var hash = ShareSecrets.HashToken(token);
        var seen = await db.ShareLinks.AsNoTracking().SingleOrDefaultAsync(l => l.TokenHash == hash, ct);
        var dead = seen is null ? null : ShareService.DeniedOutcome(seen, clock.GetUtcNow());
        if (seen is null || dead is not null)
        {
            await ShareSecrets.VerifyAgainstDummyAsync(password, ct);
            if (seen is not null)
            {
                await LogDeadLinkAsync(db, seen.Id, dead!, userAgent, clock);
            }
            return new PublicOpenResult(PublicOpenOutcome.Unavailable);
        }

        return await db.InTransactionAsync(async _ =>
        {
            await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "ShareLinks" WHERE "TokenHash" = {hash} FOR UPDATE""", ct);
            var link = await db.ShareLinks.SingleOrDefaultAsync(l => l.TokenHash == hash, ct);
            var now = clock.GetUtcNow();
            var denied = link is null ? null : ShareService.DeniedOutcome(link, now);
            if (link is null || denied is not null)
            {
                // The link died while this attempt waited for the lock.
                await ShareSecrets.VerifyAgainstDummyAsync(password, ct);
                if (link is not null && !await LoggedRecentlyAsync(db, link.Id, denied!, now))
                {
                    await LogAsync(db, link, denied!, userAgent, now);
                }
                return new PublicOpenResult(PublicOpenOutcome.Unavailable);
            }

            if (!await ShareSecrets.VerifyPasswordAsync(password, link.PasswordHash, ct))
            {
                link.WrongPasswordCount++;
                if (link.WrongPasswordCount >= ShareService.MaxWrongPasswords)
                {
                    link.BlockedAt = now;
                }
                await LogAsync(db, link, ShareAccess.BadPassword, userAgent, now);
                return new PublicOpenResult(PublicOpenOutcome.WrongPassword);
            }

            var (pdf, fileName) = await RenderAsync(db, link, now);
            link.ViewsUsed++;
            await LogAsync(db, link, ShareAccess.Opened, userAgent, now);
            return new PublicOpenResult(PublicOpenOutcome.Opened, pdf, fileName, link.DownloadEnabled);
        }, CancellationToken.None);
    }

    private static Task LogDeadLinkAsync(AnamnysDbContext db, Guid linkId, string outcome, string? userAgent, TimeProvider clock) =>
        db.InTransactionAsync(async c =>
        {
            await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "ShareLinks" WHERE "Id" = {linkId} FOR UPDATE""", c);
            var link = await db.ShareLinks.SingleOrDefaultAsync(l => l.Id == linkId, c);
            var now = clock.GetUtcNow();
            if (link is not null && !await LoggedRecentlyAsync(db, linkId, outcome, now))
            {
                await LogAsync(db, link, outcome, userAgent, now);
            }
            return true;
        }, CancellationToken.None);

    private static Task<bool> LoggedRecentlyAsync(AnamnysDbContext db, Guid linkId, string outcome, DateTimeOffset now)
    {
        var since = now - DeadLinkLogPeriod;
        return db.ShareAccesses.AnyAsync(a => a.ShareLinkId == linkId && a.Outcome == outcome && a.At > since, CancellationToken.None);
    }

    private static async Task<(byte[] Pdf, string FileName)> RenderAsync(AnamnysDbContext db, ShareLink link, DateTimeOffset now)
    {
        var ct = CancellationToken.None;
        var document = await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == link.DocumentId, ct);
        var authorization = await db.DisclosureAuthorizations.AsNoTracking().SingleAsync(a => a.Id == link.AuthorizationId, ct);
        // Signing always records the ruleset, so a signed document without one is corrupt data.
        var rulesetId = document.RulesetId ?? throw new InvalidOperationException($"Signed document {document.Id} has no ruleset.");
        var ruleset = await db.ConformityRulesets.AsNoTracking().SingleAsync(r => r.Id == rulesetId, ct);
        var patient = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == document.PatientId, ct);
        var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == document.ProviderId, ct);
        var model = DocumentPdfModel.From(document, ruleset, patient, provider);
        return (DocumentPdf.Render(model, new DocumentWatermark(authorization.RecipientName, now)), model.FileName);
    }

    private static async Task LogAsync(AnamnysDbContext db, ShareLink link, string outcome, string? userAgent, DateTimeOffset now)
    {
        db.ShareAccesses.Add(new ShareAccess
        {
            Id = Guid.NewGuid(),
            ShareLinkId = link.Id,
            At = now,
            Outcome = outcome,
            UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent[..Math.Min(userAgent.Length, MaxUserAgent)],
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
