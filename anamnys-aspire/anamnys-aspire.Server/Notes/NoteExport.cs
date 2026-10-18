using System.Security.Cryptography;
using Anamnys.Server.Data;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

public enum ExportOutcome { Ok, NotFound, NotSigned, StorageUnavailable }

public sealed record ExportResult(ExportOutcome Outcome, Uri? Url, DateTimeOffset? ExpiresAt);

// PDF export of a signed note (design/specs/2026-10-17-note-pdf-export-design.md §5.3): generated and
// stored on the first export, only presigned afterwards.
public static class NoteExport
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public static async Task<ExportResult> ExportAsync(
        AnamnysDbContext db, INoteFileStore store, Guid providerId, Guid noteId, DateTimeOffset now, CancellationToken ct)
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
            await NoteLocks.LockAsync(db, noteId, ct);

            var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == noteId && n.ProviderId == providerId, ct);
            if (note is null)
            {
                return;
            }
            if (note.Status is not ("Signed" or "Exported"))
            {
                outcome = ExportOutcome.NotSigned;
                return;
            }
            fileName = NotePdfModel.FileNameFor(note.Id);

            if (note.PdfObjectKey is null)
            {
                var sections = await db.NoteSections.AsNoTracking().Where(s => s.NoteId == noteId).ToListAsync(ct);
                var patient = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == note.PatientId && p.ProviderId == providerId, ct);
                var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == providerId, ct);
                var session = note.SessionId is { } sessionId
                    ? await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId && s.ProviderId == providerId, ct)
                    : null;
                var bytes = NotePdf.Render(NotePdfModel.From(note, sections, patient, provider, session));
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var objectKey = $"notes/{providerId}/{noteId}.pdf";
                try
                {
                    await store.PutAsync(objectKey, bytes, "application/pdf", ct);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    outcome = ExportOutcome.StorageUnavailable;
                    return;
                }
                note.PdfObjectKey = objectKey;
                note.PdfSha256 = hash;
                note.ExportedAt = now;
                note.Status = "Exported";
                note.UpdatedAt = now;
                NoteAudit.Add(db, noteId, providerId, "exported", null, null, hash, now);
            }
            else
            {
                NoteAudit.Add(db, noteId, providerId, "pdf_link_issued", null, null, null, now);
            }
            key = note.PdfObjectKey;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            outcome = ExportOutcome.Ok;
        });

        return outcome == ExportOutcome.Ok
            ? new ExportResult(outcome, store.PresignGet(key!, fileName!, LinkLifetime), now + LinkLifetime)
            : new ExportResult(outcome, null, null);
    }
}
