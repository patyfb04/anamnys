using System.Security.Cryptography;
using System.Text;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Integrity;

// Appending to a provider's chain (design/specs/2026-11-03-integrity-chain-design.md §2, §4).
public static class IntegritySeals
{
    // Namespace of the advisory locks taken here: the provider's first 8 bytes XOR this constant ("ANAMNYS1"), so the key
    // does not collide with another lock family that also keys on a provider id.
    private const long LockNamespace = 0x414E_414D_4E59_5331;

    // Hash = hex(SHA-256(UTF-8(previous ?? "" | sequence | type | subject id | content hash | sealed at))).
    public static string SealHash(
        string? previousHash, long sequence, string subjectType, Guid subjectId, string contentHash, DateTimeOffset sealedAt)
    {
        var text = string.Join('|',
            previousHash ?? "",
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            subjectType,
            subjectId.ToString("D"),
            contentHash,
            IntegrityCanonical.FormatTime(sealedAt));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    // Runs inside the caller's transaction, so a sealing failure fails the signing. The lock serializes one provider's
    // appends and is released with the transaction; providers never wait on each other.
    public static async Task<IntegritySeal> SealAsync(
        AnamnysDbContext db, Guid providerId, string subjectType, Guid subjectId, string contentHash, DateTimeOffset now,
        CancellationToken ct, bool retroactive = false)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Sealing must run inside the signing transaction.");
        }
        await LockAsync(db, providerId, ct);

        var head = await db.IntegritySeals.AsNoTracking()
            .Where(s => s.ProviderId == providerId)
            .OrderByDescending(s => s.Sequence)
            .Select(s => new { s.Sequence, s.Hash })
            .FirstOrDefaultAsync(ct);
        // Microsecond precision, like the column: what is hashed is what is stored.
        var sealedAt = new DateTimeOffset(now.UtcTicks / 10 * 10, TimeSpan.Zero);
        var sequence = (head?.Sequence ?? 0) + 1;
        var seal = new IntegritySeal
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            Sequence = sequence,
            SubjectType = subjectType,
            SubjectId = subjectId,
            ContentHash = contentHash,
            PreviousHash = head?.Hash,
            Hash = SealHash(head?.Hash, sequence, subjectType, subjectId, contentHash, sealedAt),
            SealedAt = sealedAt,
            Retroactive = retroactive,
        };
        db.IntegritySeals.Add(seal);
        await db.SaveChangesAsync(ct);
        return seal;
    }

    // Null when the note does not exist for this provider or is not signed.
    public static async Task<string?> NoteContentHashAsync(AnamnysDbContext db, Guid providerId, Guid noteId, CancellationToken ct)
    {
        var note = await db.Notes.AsNoTracking()
            .SingleOrDefaultAsync(n => n.Id == noteId && n.ProviderId == providerId && n.SignedAt != null, ct);
        if (note is null)
        {
            return null;
        }
        var sections = await db.NoteSections.AsNoTracking().Where(s => s.NoteId == noteId).ToListAsync(ct);
        return IntegrityCanonical.Hash(IntegrityCanonical.ForNote(note, sections));
    }

    // Null when the document does not exist for this provider or is not signed.
    public static async Task<string?> DocumentContentHashAsync(AnamnysDbContext db, Guid providerId, Guid documentId, CancellationToken ct)
    {
        var document = await db.ClinicalDocuments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == documentId && d.ProviderId == providerId && d.SignedAt != null, ct);
        return document is null ? null : IntegrityCanonical.Hash(IntegrityCanonical.ForDocument(document));
    }

    // The provider's append lock, held until the current transaction ends. Re-entrant within one transaction, so a caller
    // that reads before sealing (the retroactive backfill) can take it first.
    public static async Task LockAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct)
    {
        var key = LockKey(providerId);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }

    private static long LockKey(Guid providerId) => BitConverter.ToInt64(providerId.ToByteArray(), 0) ^ LockNamespace;
}
