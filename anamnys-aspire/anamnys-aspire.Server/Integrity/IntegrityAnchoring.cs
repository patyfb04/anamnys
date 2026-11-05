using System.Security.Cryptography;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Integrity;

// Timestamping a provider's chain head (design/specs/2026-11-03-integrity-chain-design.md §6). Runs outside any signing
// transaction and outside any database transaction while the authority answers: signing never waits for the TSA.
public static class IntegrityAnchoring
{
    // Providers whose head Sequence is past their last anchor's.
    public static Task<List<Guid>> PendingProvidersAsync(AnamnysDbContext db, CancellationToken ct, Guid? providerId = null) =>
        db.IntegritySeals.AsNoTracking()
            .Where(s => providerId == null || s.ProviderId == providerId)
            .GroupBy(s => s.ProviderId)
            .Select(g => new { ProviderId = g.Key, Head = g.Max(s => s.Sequence) })
            .Where(h => !db.IntegrityAnchors.Any(a => a.ProviderId == h.ProviderId && a.Sequence >= h.Head))
            .Select(h => h.ProviderId)
            .ToListAsync(ct);

    // The stored anchor, or null when there is nothing new to stamp (or another instance stored it first). Throws when the
    // authority cannot be reached or answers with a token that does not verify; nothing is stored then.
    public static async Task<IntegrityAnchor?> AnchorAsync(
        AnamnysDbContext db, Guid providerId, ITimestampAuthority tsa, CancellationToken ct)
    {
        var head = await db.IntegritySeals.AsNoTracking()
            .Where(s => s.ProviderId == providerId)
            .OrderByDescending(s => s.Sequence)
            .Select(s => new { s.Sequence, s.Hash })
            .FirstOrDefaultAsync(ct);
        if (head is null)
        {
            return null;
        }
        var last = await db.IntegrityAnchors.AsNoTracking()
            .Where(a => a.ProviderId == providerId)
            .MaxAsync(a => (long?)a.Sequence, ct);
        if (last >= head.Sequence)
        {
            return null;
        }

        var token = await tsa.StampAsync(head.Hash, ct);
        var genTime = tsa.Verify(token, head.Hash)
            ?? throw new CryptographicException("The timestamp token does not verify for the chain head.");
        var anchor = new IntegrityAnchor
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            Sequence = head.Sequence,
            HeadHash = head.Hash,
            TsaUrl = tsa.Url,
            Token = token,
            AnchoredAt = genTime.ToUniversalTime(),
        };
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "IntegrityAnchors" ("Id","ProviderId","Sequence","HeadHash","TsaUrl","Token","AnchoredAt")
            VALUES ({anchor.Id}, {anchor.ProviderId}, {anchor.Sequence}, {anchor.HeadHash}, {anchor.TsaUrl}, {anchor.Token}, {anchor.AnchoredAt})
            ON CONFLICT ("ProviderId","Sequence") DO NOTHING
            """, ct);
        return inserted == 1 ? anchor : null;
    }
}
