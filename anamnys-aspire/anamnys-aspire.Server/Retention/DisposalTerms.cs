using Anamnys.Server.Data;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Retention;

public sealed record DisposalTermItem(
    Guid Id, Guid PatientId, string PatientName, DateTimeOffset CreatedAt, DateOnly PeriodFrom, DateOnly PeriodTo,
    int GuardYears, DateOnly RetentionUntil, string Basis, IReadOnlyDictionary<string, int> Counts);

public sealed record DisposalTermLink(Uri Url, DateTimeOffset ExpiresAt);

// The provider's disposal terms (design/specs/2026-10-28-retention-panel-design.md §3, §4 "Termos de descarte").
// The PDF lives in the private bucket under an id-only key and is reached through a short-lived signed link.
public static class DisposalTerms
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public static string ObjectKeyFor(Guid providerId, Guid termId) => $"disposal-terms/{providerId}/{termId}.pdf";

    public static string CodeFor(Guid termId) => termId.ToString("N")[..8].ToUpperInvariant();

    public static string FileNameFor(Guid termId) => $"termo-descarte-{CodeFor(termId)}.pdf";

    // Oldest first, like every list of the panel.
    public static async Task<IReadOnlyList<DisposalTermItem>> ListAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct)
    {
        var rows = await (
                from t in db.DisposalTerms.AsNoTracking()
                join p in db.Patients.AsNoTracking() on t.PatientId equals p.Id
                where t.ProviderId == providerId && p.ProviderId == providerId
                orderby t.CreatedAt, t.Id
                select new { Term = t, Name = p.FirstName + " " + p.LastName })
            .ToListAsync(ct);
        return rows
            .Select(r => new DisposalTermItem(
                r.Term.Id, r.Term.PatientId, r.Name, r.Term.CreatedAt, r.Term.PeriodFrom, r.Term.PeriodTo, r.Term.GuardYears, r.Term.RetentionUntil,
                r.Term.Basis, r.Term.Counts))
            .ToList();
    }

    // Null when the term is not the provider's.
    public static async Task<DisposalTermLink?> PdfLinkAsync(
        AnamnysDbContext db, INoteFileStore store, Guid providerId, Guid termId, DateTimeOffset now, CancellationToken ct)
    {
        var key = await db.DisposalTerms.AsNoTracking()
            .Where(t => t.Id == termId && t.ProviderId == providerId)
            .Select(t => t.ObjectKey)
            .SingleOrDefaultAsync(ct);
        return key is null ? null : new DisposalTermLink(store.PresignGet(key, FileNameFor(termId), LinkLifetime), now + LinkLifetime);
    }
}
