using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Exports;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Integrity;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Dossiers;

// What one dossier is built from (design/specs/2026-11-04-patient-dossier-design.md §4, §5).
public sealed record DossierRequest(
    Guid DossierId, int Number, Guid ProviderId, Guid PatientId, string Reason, string? ReasonNote,
    DateOnly? PeriodFrom, DateOnly? PeriodTo, DateTimeOffset GeneratedAt);

// Column subset of a signed note: RawTranscript is never selected, so nothing built from this can carry it.
public sealed record DossierNote(
    Guid Id, Guid? SessionId, string Format, DateTimeOffset SignedAt, string? SignedByCrp, string? PdfObjectKey, DateTimeOffset CreatedAt);

// An external document received in the period, whatever its scan status; Filename stays inside the dossier.
public sealed record DossierExternal(
    Guid Id, string Filename, string MimeType, string? ObjectKey, string ScanStatus, DateTimeOffset UploadedAt, string Kind,
    DateOnly? DocumentDate, string? Description);

// A share link without its token and password hashes.
public sealed record DossierLink(
    Guid Id, Guid AuthorizationId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt,
    DateTimeOffset? BlockedAt, int ViewsUsed, int? ViewCap, bool DownloadEnabled);

public sealed record DossierAccess(Guid LinkId, DateTimeOffset At, string Outcome, string? UserAgent);

// The latest F-05 check of a signed document (absent when the document has none).
public sealed record DossierCompliance(Guid DocumentId, bool Passed, IReadOnlyList<string> Missing, string Mode, DateTimeOffset CheckedAt);

// The verification of one signed note or document at generation, with its seal's sequence when it has one.
public sealed record DossierIntegrity(string SubjectType, Guid SubjectId, SubjectIntegrity Result, long? Sequence);

// An anchor that covers at least one of the patient's seals and whose token verifies.
public sealed record DossierAnchor(long Sequence, string HeadHash, string TsaUrl, DateTimeOffset AnchoredAt, byte[] Token);

public sealed record DossierData(
    Provider Provider,
    Patient Patient,
    IReadOnlyList<DossierNote> Notes,
    ILookup<Guid, NoteSection> SectionsByNote,
    IReadOnlyDictionary<Guid, Session> Sessions,
    IReadOnlyList<ClinicalDocument> Documents,
    // Every signed document of the patient, also outside the period (to tell a signed correction from a draft one).
    IReadOnlySet<Guid> SignedDocumentIds,
    IReadOnlyDictionary<Guid, ConformityRuleset> Rulesets,
    IReadOnlyList<DossierExternal> Externals,
    IReadOnlyList<DisclosureAuthorization> Authorizations,
    // The kind of each disclosed document, also when it was signed outside the period.
    IReadOnlyDictionary<Guid, string> DisclosedKinds,
    IReadOnlyList<DossierLink> Links,
    ILookup<Guid, DossierAccess> AccessesByLink,
    IReadOnlyDictionary<Guid, DossierCompliance> Compliance,
    bool HasDisposalTerm,
    IReadOnlyList<DossierIntegrity> Integrity,
    IReadOnlyList<DossierAnchor> Anchors)
{
    // True when every signed note and document verified intact (an unsealed one counts as not verified).
    public bool IntegrityVerified => Integrity.All(i => i.Result.Status == SubjectStatus.Intact);

    // Replaced by a signed correction. A correction still in draft does not make the signed original "substituído": the
    // record still has no other signed version of it.
    public bool IsSuperseded(ClinicalDocument document) =>
        document.SupersededBy is { } replacement && SignedDocumentIds.Contains(replacement);

    // The official record of one patient in the requested period, read once. Every query is scoped to the provider and
    // the patient; drafts, RawTranscript, audit values and other patients' rows are never selected. Files are not loaded
    // here: the builder reads them one at a time.
    public static async Task<DossierData> LoadAsync(
        AnamnysDbContext db, ITimestampAuthority tsa, DossierRequest request, CancellationToken ct)
    {
        var providerId = request.ProviderId;
        var patientId = request.PatientId;
        var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);
        var patient = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == patientId && p.ProviderId == providerId, ct);
        bool InPeriod(DateTimeOffset at) => Contains(request, at);

        var notes = (await db.Notes.AsNoTracking()
                .Where(n => n.ProviderId == providerId && n.PatientId == patientId && n.SignedAt != null)
                .Select(n => new DossierNote(n.Id, n.SessionId, n.Format, n.SignedAt!.Value, n.SignedByCrp, n.PdfObjectKey, n.CreatedAt))
                .ToListAsync(ct))
            .Where(n => InPeriod(n.SignedAt))
            .OrderBy(n => n.SignedAt).ThenBy(n => n.Id)
            .ToList();
        var noteIds = notes.Select(n => n.Id).ToList();
        var sections = await (
            from s in db.NoteSections.AsNoTracking()
            join n in db.Notes on s.NoteId equals n.Id
            where n.ProviderId == providerId && n.PatientId == patientId && noteIds.Contains(s.NoteId)
            select s).ToListAsync(ct);
        var sessionIds = notes.Where(n => n.SessionId != null).Select(n => n.SessionId!.Value).Distinct().ToList();
        var sessions = await db.Sessions.AsNoTracking()
            .Where(s => s.ProviderId == providerId && sessionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        var signedDocuments = await db.ClinicalDocuments.AsNoTracking()
            .Where(d => d.ProviderId == providerId && d.PatientId == patientId && d.SignedAt != null)
            .ToListAsync(ct);
        var signedDocumentIds = signedDocuments.Select(d => d.Id).ToHashSet();
        var documents = signedDocuments
            .Where(d => InPeriod(d.SignedAt!.Value))
            .OrderBy(d => d.SignedAt).ThenBy(d => d.Id)
            .ToList();
        var documentIds = documents.Select(d => d.Id).ToList();
        var rulesetIds = documents.Where(d => d.RulesetId != null).Select(d => d.RulesetId!.Value).Distinct().ToList();
        var rulesets = await db.ConformityRulesets.AsNoTracking()
            .Where(r => rulesetIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct);

        var checks = await (
                from c in db.ConformityChecks.AsNoTracking()
                join d in db.ClinicalDocuments on c.DocumentId equals d.Id
                where d.ProviderId == providerId && d.PatientId == patientId && documentIds.Contains(c.DocumentId)
                select c)
            .ToListAsync(ct);
        var compliance = checks
            .GroupBy(c => c.DocumentId)
            .Select(g => g.OrderByDescending(c => c.CheckedAt).ThenByDescending(c => c.Id).First())
            .ToDictionary(c => c.DocumentId, c => new DossierCompliance(c.DocumentId, c.Passed, c.MissingElements, c.Mode, c.CheckedAt));

        var externals = (await db.ExternalDocuments.AsNoTracking()
                .Where(e => e.ProviderId == providerId && e.PatientId == patientId && e.DeletedAt == null)
                .Select(e => new DossierExternal(
                    e.Id, e.Filename, e.MimeType, e.ObjectKey, e.ScanStatus, e.UploadedAt, e.Kind, e.DocumentDate, e.Description))
                .ToListAsync(ct))
            .Where(e => InPeriod(e.UploadedAt))
            .OrderBy(e => e.UploadedAt).ThenBy(e => e.Id)
            .ToList();

        // Only authorizations on this patient's documents, recorded by this provider, with their links and accesses. An
        // authorization is in the dossier when its date, a link's creation, revocation or expiry, or an access falls in the
        // period; it then comes with all its links and accesses.
        var authorized = await (
                from a in db.DisclosureAuthorizations.AsNoTracking()
                join d in db.ClinicalDocuments on a.DocumentId equals d.Id
                where a.ProviderId == providerId && a.PatientId == patientId && d.ProviderId == providerId && d.PatientId == patientId
                select new { Authorization = a, d.Kind })
            .ToListAsync(ct);
        var allAuthorizationIds = authorized.Select(x => x.Authorization.Id).ToList();
        var allLinks = await (
                from l in db.ShareLinks.AsNoTracking()
                join d in db.ClinicalDocuments on l.DocumentId equals d.Id
                where d.ProviderId == providerId && d.PatientId == patientId && allAuthorizationIds.Contains(l.AuthorizationId)
                orderby l.CreatedAt, l.Id
                select new DossierLink(
                    l.Id, l.AuthorizationId, l.CreatedAt, l.ExpiresAt, l.RevokedAt, l.BlockedAt, l.ViewsUsed, l.ViewCap, l.DownloadEnabled))
            .ToListAsync(ct);
        var allLinkIds = allLinks.Select(l => l.Id).ToList();
        var allAccesses = await db.ShareAccesses.AsNoTracking()
            .Where(a => allLinkIds.Contains(a.ShareLinkId))
            .OrderBy(a => a.At).ThenBy(a => a.Id)
            .Select(a => new DossierAccess(a.ShareLinkId, a.At, a.Outcome, a.UserAgent))
            .ToListAsync(ct);
        var accessesByLink = allAccesses.ToLookup(a => a.LinkId);
        var linksByAuthorization = allLinks.ToLookup(l => l.AuthorizationId);
        bool LinkInPeriod(DossierLink l) =>
            InPeriod(l.CreatedAt) || InPeriod(l.ExpiresAt) || (l.RevokedAt is { } revoked && InPeriod(revoked))
            || accessesByLink[l.Id].Any(a => InPeriod(a.At));
        var kept = authorized
            .Where(x => InPeriod(x.Authorization.AuthorizedAt) || linksByAuthorization[x.Authorization.Id].Any(LinkInPeriod))
            .OrderBy(x => x.Authorization.AuthorizedAt).ThenBy(x => x.Authorization.Id)
            .ToList();
        var authorizations = kept.Select(x => x.Authorization).ToList();
        var disclosedKinds = kept.DistinctBy(x => x.Authorization.DocumentId).ToDictionary(x => x.Authorization.DocumentId, x => x.Kind);
        var keptIds = authorizations.Select(a => a.Id).ToHashSet();
        var links = allLinks.Where(l => keptIds.Contains(l.AuthorizationId)).ToList();
        var keptLinkIds = links.Select(l => l.Id).ToHashSet();
        var accesses = allAccesses.Where(a => keptLinkIds.Contains(a.LinkId)).ToList();

        var hasDisposalTerm = await db.DisposalTerms.AsNoTracking()
            .AnyAsync(t => t.ProviderId == providerId && t.PatientId == patientId, ct);

        var (integrity, anchors) = await IntegrityAsync(db, tsa, providerId, notes, documents, ct);

        return new DossierData(
            provider, patient, notes, sections.ToLookup(s => s.NoteId), sessions, documents, signedDocumentIds, rulesets, externals, authorizations,
            disclosedKinds, links, accesses.ToLookup(a => a.LinkId), compliance, hasDisposalTerm, integrity, anchors);
    }

    // The practice-local day of the instant within the requested period (bounds included; none means no bound).
    public static bool Contains(DossierRequest request, DateTimeOffset at)
    {
        var day = DateOnly.FromDateTime(ExportFormat.Local(at).DateTime);
        return (request.PeriodFrom is not { } from || day >= from) && (request.PeriodTo is not { } to || day <= to);
    }

    // Each note and document verified as the integrity endpoints do (F-45), and the anchors that cover their seals: for a
    // seal, the nearest anchor at or after it whose head is the seal at its sequence and whose token verifies.
    private static async Task<(IReadOnlyList<DossierIntegrity>, IReadOnlyList<DossierAnchor>)> IntegrityAsync(
        AnamnysDbContext db, ITimestampAuthority tsa, Guid providerId, IReadOnlyList<DossierNote> notes,
        IReadOnlyList<ClinicalDocument> documents, CancellationToken ct)
    {
        var subjects = notes.Select(n => (Type: IntegritySeal.Note, n.Id))
            .Concat(documents.Select(d => (Type: IntegritySeal.Document, d.Id)))
            .ToList();
        var subjectIds = subjects.Select(s => s.Id).ToList();
        var seals = (await db.IntegritySeals.AsNoTracking()
                .Where(s => s.ProviderId == providerId && subjectIds.Contains(s.SubjectId))
                .Select(s => new { s.SubjectType, s.SubjectId, s.Sequence })
                .ToListAsync(ct))
            .ToDictionary(s => (s.SubjectType, s.SubjectId), s => s.Sequence);

        var integrity = new List<DossierIntegrity>(subjects.Count);
        foreach (var (type, id) in subjects)
        {
            var result = await IntegrityVerifier.VerifySubjectAsync(db, providerId, type, id, tsa, ct)
                ?? new SubjectIntegrity(SubjectStatus.Unsealed, null, false, false);
            integrity.Add(new DossierIntegrity(type, id, result, seals.TryGetValue((type, id), out var sequence) ? sequence : null));
        }

        var sequences = integrity.Where(i => i.Sequence is not null).Select(i => i.Sequence!.Value).ToList();
        if (sequences.Count == 0)
        {
            return (integrity, []);
        }
        var lowest = sequences.Min();
        var candidates = await db.IntegrityAnchors.AsNoTracking()
            .Where(a => a.ProviderId == providerId && a.Sequence >= lowest)
            .OrderBy(a => a.Sequence)
            .ToListAsync(ct);
        var anchorSequences = candidates.Select(a => a.Sequence).ToList();
        var heads = await db.IntegritySeals.AsNoTracking()
            .Where(s => s.ProviderId == providerId && anchorSequences.Contains(s.Sequence))
            .ToDictionaryAsync(s => s.Sequence, s => s.Hash, ct);
        // Walk the anchors in order and stop once every seal is covered: a token is verified only when some seal still
        // waits for it. Like F-45's "anchored", the links between a seal and its anchor are not walked here (the subject
        // check covers the seal's own link only).
        var uncovered = new SortedSet<long>(sequences);
        var covering = new List<DossierAnchor>();
        foreach (var a in candidates)
        {
            if (uncovered.Count == 0)
            {
                break;
            }
            if (uncovered.Min > a.Sequence
                || heads.GetValueOrDefault(a.Sequence) != a.HeadHash || tsa.Verify(a.Token, a.HeadHash) is null)
            {
                continue;
            }
            uncovered.RemoveWhere(s => s <= a.Sequence);
            covering.Add(new DossierAnchor(a.Sequence, a.HeadHash, a.TsaUrl, a.AnchoredAt, a.Token));
        }
        return (integrity, covering);
    }

    public static bool IsIncluded(DossierExternal external) =>
        external.ScanStatus == ExternalDocumentService.Clean && external.ObjectKey is not null;
}
