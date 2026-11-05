using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Integrity;

// Records signed before F-45 (design/specs/2026-11-03-integrity-chain-design.md §1, §4): sealed once, in signing order (ties
// by id), flagged retroactive, SealedAt = the run time (never back-dated). Idempotent: sealed subjects are skipped.
public static class IntegrityBackfill
{
    private sealed record Pending(string SubjectType, Guid SubjectId, DateTimeOffset SignedAt);

    // False while design/migrations/2026-11-03-integrity-chain.sql is not applied: the server still starts.
    public static Task<bool> SchemaPresentAsync(AnamnysDbContext db, CancellationToken ct) =>
        db.Database
            .SqlQuery<bool>($"SELECT to_regclass('public.\"IntegritySeals\"') IS NOT NULL AND to_regclass('public.\"IntegrityAnchors\"') IS NOT NULL AS \"Value\"")
            .SingleAsync(ct);

    public sealed record Result(int Created, int FailedProviders);

    // providerId narrows a run to one provider (tests); production passes null. Providers are isolated: one provider's
    // failure (its transaction rolls back whole) is logged by exception type only, never the message, ids or row data, and
    // the run goes on with the next provider. Cancellation is not swallowed.
    public static async Task<Result> RunAsync(
        AnamnysDbContext db, DateTimeOffset now, CancellationToken ct, Guid? providerId = null, ILogger? logger = null)
    {
        var providers = await UnsealedNotes(db, providerId).Select(n => n.ProviderId)
            .Union(UnsealedDocuments(db, providerId).Select(d => d.ProviderId))
            .ToListAsync(ct);
        var created = 0;
        var failed = 0;
        // Ordered like PostgreSQL's uuid order, so runs are predictable.
        foreach (var provider in providers.OrderBy(p => p.ToString("D"), StringComparer.Ordinal))
        {
            try
            {
                created += await SealProviderAsync(db, provider, now, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                db.ChangeTracker.Clear();
                failed++;
                logger?.LogError("Retroactive sealing failed for one provider: {ExceptionType}", e.GetType().Name);
            }
        }
        return new Result(created, failed);
    }

    // One transaction per provider: all of a provider's retroactive seals land together, consecutive, or none do.
    private static Task<int> SealProviderAsync(AnamnysDbContext db, Guid providerId, DateTimeOffset now, CancellationToken ct) =>
        db.InTransactionAsync(work => SealProviderInTransactionAsync(db, providerId, now, work), ct);

    private static async Task<int> SealProviderInTransactionAsync(AnamnysDbContext db, Guid providerId, DateTimeOffset now, CancellationToken ct)
    {
        // Taken before reading, so a second instance running the backfill waits and then finds nothing left.
        await IntegritySeals.LockAsync(db, providerId, ct);
        var notes = await UnsealedNotes(db, providerId)
            .Select(n => new Pending(IntegritySeal.Note, n.Id, n.SignedAt!.Value))
            .ToListAsync(ct);
        var documents = await UnsealedDocuments(db, providerId)
            .Select(d => new Pending(IntegritySeal.Document, d.Id, d.SignedAt!.Value))
            .ToListAsync(ct);
        // The id as lowercase hyphenated text sorts like PostgreSQL's uuid order.
        var pending = notes.Concat(documents)
            .OrderBy(p => p.SignedAt)
            .ThenBy(p => p.SubjectId.ToString("D"), StringComparer.Ordinal)
            .ToList();

        var created = 0;
        foreach (var item in pending)
        {
            var contentHash = item.SubjectType == IntegritySeal.Note
                ? await IntegritySeals.NoteContentHashAsync(db, providerId, item.SubjectId, ct)
                : await IntegritySeals.DocumentContentHashAsync(db, providerId, item.SubjectId, ct);
            if (contentHash is null)
            {
                continue;
            }
            await IntegritySeals.SealAsync(db, providerId, item.SubjectType, item.SubjectId, contentHash, now, ct, retroactive: true);
            db.ChangeTracker.Clear();
            created++;
        }
        return created;
    }

    private static IQueryable<Note> UnsealedNotes(AnamnysDbContext db, Guid? providerId) =>
        db.Notes.AsNoTracking().Where(n => n.SignedAt != null && (providerId == null || n.ProviderId == providerId)
            && !db.IntegritySeals.Any(s => s.SubjectType == IntegritySeal.Note && s.SubjectId == n.Id));

    private static IQueryable<ClinicalDocument> UnsealedDocuments(AnamnysDbContext db, Guid? providerId) =>
        db.ClinicalDocuments.AsNoTracking().Where(d => d.SignedAt != null && (providerId == null || d.ProviderId == providerId)
            && !db.IntegritySeals.Any(s => s.SubjectType == IntegritySeal.Document && s.SubjectId == d.Id));
}

// Runs the backfill once at startup, after the schema bootstrap. Never blocks the server from starting: a failure is
// logged (type only, no row data) and retried at the next start.
public sealed class IntegrityBackfillService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<IntegrityBackfillService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            if (!await IntegrityBackfill.SchemaPresentAsync(db, cancellationToken))
            {
                logger.LogWarning("Integrity tables not present; retroactive sealing skipped.");
                return;
            }
            var result = await IntegrityBackfill.RunAsync(db, clock.GetUtcNow(), cancellationToken, logger: logger);
            logger.LogInformation("Retroactive sealing created {Count} seals; {Failed} providers failed.", result.Created, result.FailedProviders);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Retroactive sealing failed: {ExceptionType}", e.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
