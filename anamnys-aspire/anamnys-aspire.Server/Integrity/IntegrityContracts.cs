namespace Anamnys.Server.Integrity;

// Answers of the integrity endpoints (design/specs/2026-11-03-integrity-chain-design.md §5, §8): ids, dates and results only.
// Serialized camelCase: { status, sealedAt, retroactive, anchored } and
// { totalSeals, discarded, firstProblem: { sequence, subjectType, kind } | null, lastAnchor: { sequence, anchoredAt, tsaUrl } | null, verifiedAt }.
public sealed record SubjectIntegrity(string Status, DateTimeOffset? SealedAt, bool Retroactive, bool Anchored);

public sealed record ChainProblem(long Sequence, string? SubjectType, string Kind);

public sealed record AnchorInfo(long Sequence, DateTimeOffset AnchoredAt, string TsaUrl);

public sealed record ChainIntegrity(long TotalSeals, long Discarded, ChainProblem? FirstProblem, AnchorInfo? LastAnchor, DateTimeOffset VerifiedAt);

public static class SubjectStatus
{
    public const string Intact = "intact";
    public const string ContentChanged = "content_changed";
    public const string ChainBroken = "chain_broken";
    public const string Unsealed = "unsealed";
    public const string Discarded = "discarded";
}

// The kinds of ChainProblem.
public static class ChainProblemKind
{
    // A Sequence is missing (a seal was removed); the problem's sequence is the missing one.
    public const string SequenceGap = "sequence_gap";
    // A seal's Hash does not recompute or its PreviousHash is not the previous seal's Hash.
    public const string ChainBroken = "chain_broken";
    // A non-discarded subject's canonical content no longer hashes to the sealed ContentHash (or the record is gone).
    public const string ContentChanged = "content_changed";
    // A timestamp token does not verify, or its HeadHash is not the Hash of the seal at its Sequence.
    public const string AnchorInvalid = "anchor_invalid";
}
