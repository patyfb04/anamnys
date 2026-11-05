using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Integrity;

// Checking a provider's chain (design/specs/2026-11-03-integrity-chain-design.md §5). Every query is scoped to the provider:
// one provider never sees another's seals, anchors or records.
public static class IntegrityVerifier
{
    public const int BatchSize = 500;

    internal sealed record AnchorCheck(IntegrityAnchor Anchor, bool Valid);

    // Null when the subject is neither sealed nor signed for this provider (unknown, foreign or a draft).
    public static async Task<SubjectIntegrity?> VerifySubjectAsync(
        AnamnysDbContext db, Guid providerId, string subjectType, Guid subjectId, ITimestampAuthority tsa, CancellationToken ct)
    {
        if (subjectType is not (IntegritySeal.Note or IntegritySeal.Document))
        {
            return null;
        }
        var seal = await db.IntegritySeals.AsNoTracking()
            .SingleOrDefaultAsync(s => s.ProviderId == providerId && s.SubjectType == subjectType && s.SubjectId == subjectId, ct);
        if (seal is null)
        {
            var signed = subjectType == IntegritySeal.Note
                ? await db.Notes.AsNoTracking().AnyAsync(n => n.Id == subjectId && n.ProviderId == providerId && n.SignedAt != null, ct)
                : await db.ClinicalDocuments.AsNoTracking().AnyAsync(d => d.Id == subjectId && d.ProviderId == providerId && d.SignedAt != null, ct);
            return signed ? new SubjectIntegrity(SubjectStatus.Unsealed, null, false, false) : null;
        }

        var anchored = await AnchoredAsync(db, providerId, seal.Sequence, tsa, ct);
        string status;
        if (seal.DiscardedAt is not null)
        {
            status = SubjectStatus.Discarded;
        }
        else if (!await LinkHoldsAsync(db, seal, ct))
        {
            status = SubjectStatus.ChainBroken;
        }
        else
        {
            var content = subjectType == IntegritySeal.Note
                ? await IntegritySeals.NoteContentHashAsync(db, providerId, subjectId, ct)
                : await IntegritySeals.DocumentContentHashAsync(db, providerId, subjectId, ct);
            status = content == seal.ContentHash ? SubjectStatus.Intact : SubjectStatus.ContentChanged;
        }
        return new SubjectIntegrity(status, seal.SealedAt, seal.Retroactive, anchored);
    }

    public static async Task<ChainIntegrity> VerifyChainAsync(
        AnamnysDbContext db, Guid providerId, ITimestampAuthority tsa, DateTimeOffset now, CancellationToken ct) =>
        (await VerifyChainDetailedAsync(db, providerId, tsa, now, ct)).Chain;

    // The summary plus every anchor with its result, for the verification report.
    internal static async Task<(ChainIntegrity Chain, IReadOnlyList<AnchorCheck> Anchors, IntegritySeal? Head)> VerifyChainDetailedAsync(
        AnamnysDbContext db, Guid providerId, ITimestampAuthority tsa, DateTimeOffset now, CancellationToken ct)
    {
        var anchors = await db.IntegrityAnchors.AsNoTracking()
            .Where(a => a.ProviderId == providerId)
            .OrderBy(a => a.Sequence)
            .ToListAsync(ct);
        var anchorSequences = anchors.Select(a => a.Sequence).ToHashSet();
        var hashAt = new Dictionary<long, string>();

        long total = 0, discarded = 0, expected = 1, after = 0;
        string? previousHash = null;
        IntegritySeal? head = null;
        ChainProblem? problem = null;
        while (true)
        {
            var batch = await db.IntegritySeals.AsNoTracking()
                .Where(s => s.ProviderId == providerId && s.Sequence > after)
                .OrderBy(s => s.Sequence)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }
            var contents = await ContentHashesAsync(db, providerId, batch.Where(s => s.DiscardedAt is null).ToList(), ct);
            foreach (var seal in batch)
            {
                total++;
                if (seal.DiscardedAt is not null)
                {
                    discarded++;
                }
                if (anchorSequences.Contains(seal.Sequence))
                {
                    hashAt[seal.Sequence] = seal.Hash;
                }
                if (problem is null)
                {
                    if (seal.Sequence != expected)
                    {
                        problem = new ChainProblem(expected, null, ChainProblemKind.SequenceGap);
                    }
                    else if (seal.PreviousHash != previousHash || Recompute(seal) != seal.Hash)
                    {
                        problem = new ChainProblem(seal.Sequence, seal.SubjectType, ChainProblemKind.ChainBroken);
                    }
                    else if (seal.DiscardedAt is null && contents.GetValueOrDefault((seal.SubjectType, seal.SubjectId)) != seal.ContentHash)
                    {
                        problem = new ChainProblem(seal.Sequence, seal.SubjectType, ChainProblemKind.ContentChanged);
                    }
                }
                expected = seal.Sequence + 1;
                previousHash = seal.Hash;
                head = seal;
            }
            after = batch[^1].Sequence;
        }

        var checks = new List<AnchorCheck>(anchors.Count);
        AnchorInfo? lastAnchor = null;
        foreach (var anchor in anchors)
        {
            var valid = hashAt.TryGetValue(anchor.Sequence, out var hash) && hash == anchor.HeadHash
                && tsa.Verify(anchor.Token, anchor.HeadHash) is not null;
            checks.Add(new AnchorCheck(anchor, valid));
            if (valid)
            {
                lastAnchor = new AnchorInfo(anchor.Sequence, anchor.AnchoredAt, anchor.TsaUrl);
            }
            else if (problem is null || anchor.Sequence < problem.Sequence)
            {
                problem = new ChainProblem(anchor.Sequence, null, ChainProblemKind.AnchorInvalid);
            }
        }
        return (new ChainIntegrity(total, discarded, problem, lastAnchor, now), checks, head);
    }

    private static string Recompute(IntegritySeal seal) =>
        IntegritySeals.SealHash(seal.PreviousHash, seal.Sequence, seal.SubjectType, seal.SubjectId, seal.ContentHash, seal.SealedAt);

    // The seal's own hash recomputes and it points to the previous seal's hash (none for the first).
    private static async Task<bool> LinkHoldsAsync(AnamnysDbContext db, IntegritySeal seal, CancellationToken ct)
    {
        if (Recompute(seal) != seal.Hash)
        {
            return false;
        }
        if (seal.Sequence == 1)
        {
            return seal.PreviousHash is null;
        }
        var previous = await db.IntegritySeals.AsNoTracking()
            .Where(s => s.ProviderId == seal.ProviderId && s.Sequence == seal.Sequence - 1)
            .Select(s => s.Hash)
            .SingleOrDefaultAsync(ct);
        return previous is not null && previous == seal.PreviousHash;
    }

    // A valid anchor at or after the seal: its HeadHash is the Hash of the seal at its Sequence and its token verifies
    // (signature, signer chained to the configured root, imprint = SHA-256 of HeadHash). A row alone proves nothing: the
    // nearest anchors are tried first and the walk stops at the first that verifies.
    private static async Task<bool> AnchoredAsync(
        AnamnysDbContext db, Guid providerId, long sequence, ITimestampAuthority tsa, CancellationToken ct)
    {
        var candidates =
            from a in db.IntegrityAnchors.AsNoTracking()
            join s in db.IntegritySeals.AsNoTracking() on new { a.ProviderId, a.Sequence } equals new { s.ProviderId, s.Sequence }
            where a.ProviderId == providerId && a.Sequence >= sequence && s.Hash == a.HeadHash
            orderby a.Sequence
            select new { a.HeadHash, a.Token };
        await foreach (var anchor in candidates.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (tsa.Verify(anchor.Token, anchor.HeadHash) is not null)
            {
                return true;
            }
        }
        return false;
    }

    // The current content hash of each sealed subject in the batch that still exists, is the provider's and is signed.
    private static async Task<Dictionary<(string, Guid), string>> ContentHashesAsync(
        AnamnysDbContext db, Guid providerId, IReadOnlyList<IntegritySeal> seals, CancellationToken ct)
    {
        var result = new Dictionary<(string, Guid), string>();
        var noteIds = seals.Where(s => s.SubjectType == IntegritySeal.Note).Select(s => s.SubjectId).ToList();
        if (noteIds.Count > 0)
        {
            var notes = await db.Notes.AsNoTracking()
                .Where(n => n.ProviderId == providerId && n.SignedAt != null && noteIds.Contains(n.Id))
                .ToListAsync(ct);
            var found = notes.Select(n => n.Id).ToList();
            var sections = (await db.NoteSections.AsNoTracking().Where(s => found.Contains(s.NoteId)).ToListAsync(ct))
                .ToLookup(s => s.NoteId);
            foreach (var note in notes)
            {
                result[(IntegritySeal.Note, note.Id)] = IntegrityCanonical.Hash(IntegrityCanonical.ForNote(note, sections[note.Id].ToList()));
            }
        }
        var documentIds = seals.Where(s => s.SubjectType == IntegritySeal.Document).Select(s => s.SubjectId).ToList();
        if (documentIds.Count > 0)
        {
            var documents = await db.ClinicalDocuments.AsNoTracking()
                .Where(d => d.ProviderId == providerId && d.SignedAt != null && documentIds.Contains(d.Id))
                .ToListAsync(ct);
            foreach (var document in documents)
            {
                result[(IntegritySeal.Document, document.Id)] = IntegrityCanonical.Hash(IntegrityCanonical.ForDocument(document));
            }
        }
        return result;
    }
}
