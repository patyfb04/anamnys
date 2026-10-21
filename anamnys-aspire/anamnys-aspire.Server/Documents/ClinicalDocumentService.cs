using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Anamnys.Server.Documents;

public sealed record DocumentSummary(
    Guid Id, string Kind, string Title, string Status, DateTimeOffset CreatedAt, DateTimeOffset? SignedAt, Guid? SupersededBy);

public sealed record DocumentDetail(
    Guid Id, Guid PatientId, string PatientName, string Kind, string Title, string Status,
    IReadOnlyDictionary<string, string> Fields, IReadOnlyList<string> Confirmations, ConformityResult Check,
    string Mode, DateTimeOffset CreatedAt, DateTimeOffset? SignedAt, string? SignedByCrp, Guid? SupersededBy,
    DateTimeOffset? ExportedAt, bool ProviderHasCrp, bool HasPracticeCity,
    string? SignedCity, string? SignedByName, string? SignedPatientName, IReadOnlyList<TeamMember> Team);

public enum DocumentOutcome
{
    Ok, NotFound, Archived, Locked, KindNotAvailable, MissingElements, ConfirmationsRequired, MissingCrp, MissingCity, Invalid,
    TeamNotAllowed,
}

public sealed record DocumentResult(DocumentOutcome Outcome, Guid? Id = null, IReadOnlyList<string>? Missing = null);

public static class ClinicalDocumentService
{
    public const int MaxFieldLength = 20_000;

    public static async Task<DocumentResult> CreateAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, string kind, Guid? noteId, DateTimeOffset now, CancellationToken ct)
    {
        if (!DocumentCatalog.AvailableKinds.Contains(kind))
        {
            return new DocumentResult(DocumentOutcome.KindNotAvailable);
        }

        return await InTransactionAsync(db, async () =>
        {
            var patient = await db.Patients.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Id == patientId && p.ProviderId == providerId, ct);
            if (patient is null)
            {
                return new DocumentResult(DocumentOutcome.NotFound);
            }
            if (patient.ArchivedAt is not null)
            {
                return new DocumentResult(DocumentOutcome.Archived);
            }
            if (noteId is not null
                && !await db.Notes.AnyAsync(n => n.Id == noteId && n.ProviderId == providerId && n.PatientId == patientId, ct))
            {
                return new DocumentResult(DocumentOutcome.NotFound);
            }
            var ruleset = await LatestRulesetAsync(db, kind, ct);
            if (ruleset is null)
            {
                return new DocumentResult(DocumentOutcome.KindNotAvailable);
            }

            var document = new ClinicalDocument
            {
                Id = Guid.NewGuid(),
                PatientId = patientId,
                ProviderId = providerId,
                NoteId = noteId,
                Kind = kind,
                RulesetId = ruleset.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ClinicalDocuments.Add(document);
            await db.SaveChangesAsync(ct);
            DocumentAudit.Add(db, document.Id, providerId, "document_created", null, null, kind, now);
            await db.SaveChangesAsync(ct);
            return new DocumentResult(DocumentOutcome.Ok, document.Id);
        }, ct);
    }

    public static async Task<IReadOnlyList<DocumentSummary>?> ListAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.ProviderId == providerId, ct))
        {
            return null;
        }
        var rows = await db.ClinicalDocuments.AsNoTracking()
            .Where(d => d.PatientId == patientId && d.ProviderId == providerId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        return rows
            .Select(d => new DocumentSummary(
                d.Id, d.Kind, DocumentCatalog.KindTitle(d.Kind), StatusOf(d), d.CreatedAt, d.SignedAt, d.SupersededBy))
            .ToList();
    }

    public static async Task<DocumentDetail?> GetAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, CancellationToken ct)
    {
        var document = await db.ClinicalDocuments.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == documentId && d.ProviderId == providerId, ct);
        if (document is null)
        {
            return null;
        }
        var patient = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == document.PatientId, ct);
        var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);
        var ruleset = await RulesetOfAsync(db, document, ct);
        var check = Check(document, ruleset, provider);
        var mode = provider.DocumentCheckMode;
        if (document.SignedAt is not null)
        {
            var signedCheck = await db.ConformityChecks.AsNoTracking()
                .Where(c => c.DocumentId == documentId)
                .OrderByDescending(c => c.CheckedAt)
                .FirstOrDefaultAsync(ct);
            if (signedCheck is not null)
            {
                mode = signedCheck.Mode;
                check = new ConformityResult(signedCheck.MissingElements, []);
            }
        }
        var signed = document.SignedAt is not null;
        return new DocumentDetail(
            document.Id, document.PatientId, document.SignedPatientName ?? $"{patient.FirstName} {patient.LastName}".Trim(),
            document.Kind, DocumentCatalog.KindTitle(document.Kind), StatusOf(document), document.Fields, document.Confirmations,
            check, mode, document.CreatedAt, document.SignedAt, document.SignedByCrp, document.SupersededBy, document.ExportedAt,
            signed ? !string.IsNullOrWhiteSpace(document.SignedByCrp) : HasCrp(provider),
            signed ? !string.IsNullOrWhiteSpace(document.SignedCity) : !string.IsNullOrWhiteSpace(provider.PracticeCity),
            document.SignedCity, document.SignedByName, document.SignedPatientName, document.Team);
    }

    public static async Task<DocumentResult> SaveAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, IReadOnlyDictionary<string, string> fields,
        IReadOnlyCollection<string> confirmations, IReadOnlyList<TeamMember> team, DateTimeOffset now, CancellationToken ct)
    {
        return await InTransactionAsync(db, async () =>
        {
            await DocumentLocks.LockAsync(db, providerId, documentId, ct);
            var document = await db.ClinicalDocuments
                .SingleOrDefaultAsync(d => d.Id == documentId && d.ProviderId == providerId, ct);
            if (document is null)
            {
                return new DocumentResult(DocumentOutcome.NotFound);
            }
            if (document.SignedAt is not null)
            {
                return new DocumentResult(DocumentOutcome.Locked);
            }
            var ruleset = await RulesetOfAsync(db, document, ct);
            var providerCodes = DocumentBody.ProviderFieldOrder(document.Kind, ruleset.Elements);

            var kept = new Dictionary<string, string>();
            foreach (var (code, value) in fields)
            {
                if (providerCodes.Contains(code))
                {
                    kept[code] = (value ?? "").Trim();
                }
            }
            var tooLong = kept.Where(kv => kv.Value.Length > MaxFieldLength).Select(kv => kv.Key).Order().ToList();
            if (tooLong.Count > 0)
            {
                return new DocumentResult(DocumentOutcome.Invalid, Missing: tooLong);
            }

            var nextConfirmations = confirmations.Where(ruleset.Prohibitions.Contains).Distinct().ToList();
            var changedCodes = kept.Keys.Concat(document.Fields.Keys).Distinct().Order()
                .Where(code => document.Fields.GetValueOrDefault(code, "") != kept.GetValueOrDefault(code, ""))
                .ToList();
            var previousSet = string.Join(",", document.Confirmations.Order());
            var nextSet = string.Join(",", nextConfirmations.Order());
            var nextTeam = team
                .Select(m => new TeamMember(m.Name.Trim(), m.Category.Trim(), m.Registry.Trim(), m.Analysis.Trim()))
                .Where(m => m.Name.Length + m.Category.Length + m.Registry.Length + m.Analysis.Length > 0)
                .ToList();
            if (nextTeam.Count > 0 && document.Kind != "relatorio_multiprofissional")
            {
                return new DocumentResult(DocumentOutcome.TeamNotAllowed);
            }
            var teamChanged = !nextTeam.SequenceEqual(document.Team);
            if (changedCodes.Count == 0 && previousSet == nextSet && !teamChanged)
            {
                return new DocumentResult(DocumentOutcome.Ok, document.Id);
            }

            foreach (var code in changedCodes)
            {
                DocumentAudit.Add(db, document.Id, providerId, "document_saved", code,
                    document.Fields.GetValueOrDefault(code, ""), kept.GetValueOrDefault(code, ""), now);
            }
            if (previousSet != nextSet)
            {
                DocumentAudit.Add(db, document.Id, providerId, "document_saved", "confirmations", previousSet, nextSet, now);
            }
            if (teamChanged)
            {
                DocumentAudit.Add(db, document.Id, providerId, "document_saved", "team",
                    JsonSerializer.Serialize(document.Team), JsonSerializer.Serialize(nextTeam), now);
                document.Team = nextTeam;
            }
            document.Fields = kept;
            document.Confirmations = nextConfirmations;
            document.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return new DocumentResult(DocumentOutcome.Ok, document.Id);
        }, ct);
    }

    public static async Task<DocumentResult> SignAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, bool acknowledgeMissing, DateTimeOffset now, CancellationToken ct)
    {
        return await InTransactionAsync(db, async () =>
        {
            await DocumentLocks.LockAsync(db, providerId, documentId, ct);
            var document = await db.ClinicalDocuments
                .SingleOrDefaultAsync(d => d.Id == documentId && d.ProviderId == providerId, ct);
            if (document is null)
            {
                return new DocumentResult(DocumentOutcome.NotFound);
            }
            if (document.SignedAt is not null)
            {
                return new DocumentResult(DocumentOutcome.Locked);
            }
            var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);
            if (!HasCrp(provider))
            {
                return new DocumentResult(DocumentOutcome.MissingCrp);
            }
            if (string.IsNullOrWhiteSpace(provider.PracticeCity))
            {
                return new DocumentResult(DocumentOutcome.MissingCity);
            }
            var ruleset = await RulesetOfAsync(db, document, ct);
            var check = Check(document, ruleset, provider);
            if (!check.ConfirmationsComplete)
            {
                return new DocumentResult(DocumentOutcome.ConfirmationsRequired, Missing: check.MissingConfirmations);
            }
            var blocking = provider.DocumentCheckMode != "advisory";
            if (!check.ElementsComplete && (blocking || !acknowledgeMissing))
            {
                return new DocumentResult(DocumentOutcome.MissingElements, Missing: check.MissingElements);
            }

            var patient = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == document.PatientId, ct);
            document.SignedByCrp = $"{provider.CrpNumber}/{provider.CrpRegion}";
            document.SignedByName = provider.Name;
            document.SignedPatientName = $"{patient.FirstName} {patient.LastName}".Trim();
            document.Body = DocumentBody.Assemble(
                document.Kind, ruleset.Elements, document.Fields, document.Team,
                document.SignedByName, document.SignedByCrp, document.SignedPatientName);
            document.SignedAt = now;
            document.SignedCity = provider.PracticeCity!.Trim();
            document.UpdatedAt = now;
            db.ConformityChecks.Add(new ConformityCheck
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                RulesetId = ruleset.Id,
                MissingElements = check.MissingElements.ToList(),
                ForbiddenFound = [],
                Mode = provider.DocumentCheckMode,
                Passed = check.ElementsComplete,
                CheckedAt = now,
            });
            DocumentAudit.Add(db, document.Id, providerId, "document_signed", null, null, document.SignedByCrp, now);
            await db.SaveChangesAsync(ct);
            return new DocumentResult(DocumentOutcome.Ok, document.Id);
        }, ct);
    }

    public static async Task<DocumentResult> CorrectAsync(
        AnamnysDbContext db, Guid providerId, Guid documentId, DateTimeOffset now, CancellationToken ct)
    {
        return await InTransactionAsync(db, async () =>
        {
            await DocumentLocks.LockAsync(db, providerId, documentId, ct);
            var original = await db.ClinicalDocuments
                .SingleOrDefaultAsync(d => d.Id == documentId && d.ProviderId == providerId, ct);
            if (original is null)
            {
                return new DocumentResult(DocumentOutcome.NotFound);
            }
            if (original.SignedAt is null || original.SupersededBy is not null)
            {
                return new DocumentResult(DocumentOutcome.Locked);
            }
            if (await db.Patients.AnyAsync(p => p.Id == original.PatientId && p.ArchivedAt != null, ct))
            {
                return new DocumentResult(DocumentOutcome.Archived);
            }

            var copy = new ClinicalDocument
            {
                Id = Guid.NewGuid(),
                PatientId = original.PatientId,
                ProviderId = providerId,
                NoteId = original.NoteId,
                Kind = original.Kind,
                RulesetId = original.RulesetId,
                Fields = new Dictionary<string, string>(original.Fields),
                Team = original.Team.ToList(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ClinicalDocuments.Add(copy);
            await db.SaveChangesAsync(ct);
            original.SupersededBy = copy.Id;
            original.UpdatedAt = now;
            DocumentAudit.Add(db, original.Id, providerId, "document_corrected", "SupersededBy", null, copy.Id.ToString(), now);
            DocumentAudit.Add(db, copy.Id, providerId, "document_created", "CorrectionOf", null, original.Id.ToString(), now);
            await db.SaveChangesAsync(ct);
            return new DocumentResult(DocumentOutcome.Ok, copy.Id);
        }, ct);
    }

    private static string StatusOf(ClinicalDocument d) =>
        d.SupersededBy is not null ? "superseded" : d.SignedAt is not null ? "signed" : "draft";

    private static bool HasCrp(Provider p) =>
        !string.IsNullOrWhiteSpace(p.CrpNumber) && !string.IsNullOrWhiteSpace(p.CrpRegion);

    private static Task<ConformityRuleset?> LatestRulesetAsync(AnamnysDbContext db, string kind, CancellationToken ct) =>
        db.ConformityRulesets.AsNoTracking()
            .Where(r => r.DocumentKind == kind)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(ct);

    private static async Task<ConformityRuleset> RulesetOfAsync(AnamnysDbContext db, ClinicalDocument d, CancellationToken ct) =>
        d.RulesetId is { } id
            ? await db.ConformityRulesets.AsNoTracking().SingleAsync(r => r.Id == id, ct)
            : await LatestRulesetAsync(db, d.Kind, ct) ?? throw new InvalidOperationException($"No ruleset for {d.Kind}.");

    private static ConformityResult Check(ClinicalDocument d, ConformityRuleset ruleset, Provider provider) =>
        DocumentConformity.Check(new ConformityInput(
            d.Kind, ruleset.Elements, ruleset.Prohibitions, d.Fields, d.Confirmations,
            HasCrp(provider), !string.IsNullOrWhiteSpace(provider.PracticeCity), d.Team));

    // Unlike Data/DbTransactions, commits only on Ok: refusals must leave no trace.
    private static async Task<DocumentResult> InTransactionAsync(
        AnamnysDbContext db, Func<Task<DocumentResult>> body, CancellationToken ct)
    {
        var result = new DocumentResult(DocumentOutcome.NotFound);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            result = await body();
            if (result.Outcome == DocumentOutcome.Ok)
            {
                await tx.CommitAsync(ct);
            }
        });
        return result;
    }
}
