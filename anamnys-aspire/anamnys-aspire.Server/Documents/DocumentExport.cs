using System.Security.Cryptography;
using Anamnys.Server.Data;
using Anamnys.Server.Notes;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Documents;

// PDF export of a signed document (design/specs/2026-10-19-psychological-documents-design.md, section 5):
// generated and stored on the first export, only presigned afterwards. A superseded document stays
// a valid historical document and can still be exported.
public static class DocumentExport
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public static async Task<ExportResult> ExportAsync(
        AnamnysDbContext db, INoteFileStore store, Guid providerId, Guid documentId, DateTimeOffset now, CancellationToken ct)
    {
        var outcome = ExportOutcome.NotFound;
        string? key = null;
        string? fileName = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            outcome = ExportOutcome.NotFound;
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await DocumentLocks.LockAsync(db, providerId, documentId, ct);

            var document = await db.ClinicalDocuments.SingleOrDefaultAsync(d => d.Id == documentId && d.ProviderId == providerId, ct);
            if (document is null)
            {
                return;
            }
            if (document.SignedAt is null)
            {
                outcome = ExportOutcome.NotSigned;
                return;
            }
            fileName = DocumentPdfModel.FileNameFor(document.Id);

            if (document.PdfObjectKey is null)
            {
                // Signing always records the ruleset, so a signed document without one is corrupt data.
                var rulesetId = document.RulesetId
                    ?? throw new InvalidOperationException($"Signed document {documentId} has no ruleset.");
                var ruleset = await db.ConformityRulesets.AsNoTracking().SingleAsync(r => r.Id == rulesetId, ct);
                var patient = await db.Patients.AsNoTracking()
                    .SingleAsync(p => p.Id == document.PatientId && p.ProviderId == providerId, ct);
                var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);
                var bytes = DocumentPdf.Render(DocumentPdfModel.From(document, ruleset, patient, provider));
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var objectKey = $"documents/{providerId}/{documentId}.pdf";
                try
                {
                    await store.PutAsync(objectKey, bytes, "application/pdf", ct);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    outcome = ExportOutcome.StorageUnavailable;
                    return;
                }
                document.PdfObjectKey = objectKey;
                document.PdfSha256 = hash;
                document.ExportedAt = now;
                document.UpdatedAt = now;
                DocumentAudit.Add(db, documentId, providerId, "document_exported", null, null, hash, now);
            }
            else
            {
                DocumentAudit.Add(db, documentId, providerId, "document_link_issued", null, null, null, now);
            }
            key = document.PdfObjectKey;
            // The PDF is already in the store: a caller cancellation here must not orphan it, so the writes ignore ct.
            await db.SaveChangesAsync(CancellationToken.None);
            await tx.CommitAsync(CancellationToken.None);
            outcome = ExportOutcome.Ok;
        });

        return outcome == ExportOutcome.Ok
            ? new ExportResult(outcome, store.PresignGet(key!, fileName!, LinkLifetime), now + LinkLifetime)
            : new ExportResult(outcome, null, null);
    }
}
