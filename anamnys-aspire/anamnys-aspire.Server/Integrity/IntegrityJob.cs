using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Integrity;

public sealed record IntegrityRunResult(int Anchored, int AnchorFailures, int Problems, int Notified);

// The nightly run (design/specs/2026-11-03-integrity-chain-design.md §7): timestamp each provider's new head, then verify
// each chain. A problem raises one in-app notice (DedupeKey integrity:<sequence>:<kind>, unique per provider, so the same
// problem is not repeated every night) and one audit entry integrity_check_failed carrying the sequence and kind only.
public static class IntegrityJobSteps
{
    public const string ProblemKind = "integrity_problem";
    public const string Subject = "integrity";
    public const string CheckFailedAction = "integrity_check_failed";

    public static async Task<IntegrityRunResult> RunAsync(
        AnamnysDbContext db, ITimestampAuthority tsa, bool stamp, ILogger logger, DateTimeOffset now, CancellationToken ct, Guid? providerId = null)
    {
        int anchored = 0, failures = 0, problems = 0, notified = 0;
        if (stamp)
        {
            foreach (var provider in await IntegrityAnchoring.PendingProvidersAsync(db, ct, providerId))
            {
                try
                {
                    if (await IntegrityAnchoring.AnchorAsync(db, provider, tsa, ct) is not null)
                    {
                        anchored++;
                    }
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    // Retried the next night. Type only: nothing about the chain or the records is logged.
                    failures++;
                    logger.LogWarning("Integrity timestamping failed: {ExceptionType}", e.GetType().Name);
                }
            }
        }

        var providers = await db.IntegritySeals.AsNoTracking()
            .Where(s => providerId == null || s.ProviderId == providerId)
            .Select(s => s.ProviderId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var provider in providers)
        {
            var chain = await IntegrityVerifier.VerifyChainAsync(db, provider, tsa, now, ct);
            if (chain.FirstProblem is not { } problem)
            {
                continue;
            }
            problems++;
            if (await NotifyAsync(db, provider, problem, now, ct))
            {
                notified++;
            }
        }
        return new IntegrityRunResult(anchored, failures, problems, notified);
    }

    // True when the notice is new; the audit entry is written with it, in the same transaction.
    private static Task<bool> NotifyAsync(AnamnysDbContext db, Guid providerId, ChainProblem problem, DateTimeOffset now, CancellationToken ct) =>
        db.InTransactionAsync(work => NotifyInTransactionAsync(db, providerId, problem, now, work), ct);

    private static async Task<bool> NotifyInTransactionAsync(AnamnysDbContext db, Guid providerId, ChainProblem problem, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        var dedupeKey = $"integrity:{problem.Sequence}:{problem.Kind}";
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Notifications" ("ProviderId","Kind","SubjectType","SubjectId","Channel","ScheduledFor","SentAt","DeliveryStatus","DedupeKey")
            VALUES ({providerId}, {ProblemKind}, {Subject}, NULL, 'in_app', {nowUtc}, {nowUtc}, 'sent', {dedupeKey})
            ON CONFLICT ("ProviderId","DedupeKey") WHERE "DedupeKey" IS NOT NULL DO NOTHING
            """, ct);
        if (inserted == 0)
        {
            return false;
        }
        db.AuditEntries.Add(new AuditEntry
        {
            Id = Guid.NewGuid(),
            ActorId = providerId,
            Action = CheckFailedAction,
            FieldChanged = Subject,
            NewValue = $"{problem.Sequence} {problem.Kind}",
            At = nowUtc,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }
}

// Runs IntegrityJobSteps at start and then once a day, on the RetentionJob pattern.
public sealed class IntegrityJob(
    IServiceScopeFactory scopes, IntegrityOptions options, TimeProvider clock, ILogger<IntegrityJob> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CanStamp)
        {
            logger.LogWarning("No TSA root certificate configured: the integrity chain is verified but not timestamped.");
        }
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
                if (!await IntegrityBackfill.SchemaPresentAsync(db, stoppingToken))
                {
                    logger.LogWarning("Integrity tables not present; integrity run skipped.");
                    continue;
                }
                var tsa = scope.ServiceProvider.GetRequiredService<ITimestampAuthority>();
                var result = await IntegrityJobSteps.RunAsync(db, tsa, options.CanStamp, logger, clock.GetUtcNow(), stoppingToken);
                logger.LogInformation(
                    "Integrity run: {Anchored} anchored, {Failures} timestamp failures, {Problems} chains with a problem, {Notified} new notices.",
                    result.Anchored, result.AnchorFailures, result.Problems, result.Notified);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Integrity run failed: {ExceptionType}", e.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
