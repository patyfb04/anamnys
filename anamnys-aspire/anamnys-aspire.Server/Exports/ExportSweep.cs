using Anamnys.Server.Data;
using Anamnys.Server.Storage;

namespace Anamnys.Server.Exports;

// At start: exports left queued or running by a previous process lost their job with it, so they
// are failed (like OrphanNoteSweep). Then hourly: ZIPs older than the retention are deleted.
public sealed class ExportSweep(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ExportSweep> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private readonly DateTimeOffset startedAt = clock.GetUtcNow();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(failInterrupted: true, stoppingToken);
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SweepAsync(failInterrupted: false, stoppingToken);
        }
    }

    private async Task SweepAsync(bool failInterrupted, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            if (failInterrupted)
            {
                var failed = await AccountExportService.FailInterruptedAsync(db, startedAt, ct);
                if (failed > 0)
                {
                    logger.LogWarning("Marked {Count} interrupted account exports as failed.", failed);
                }
            }
            var expired = await AccountExportService.ExpireAsync(
                db, scope.ServiceProvider.GetRequiredService<INoteFileStore>(), clock.GetUtcNow(), logger, ct);
            if (expired > 0)
            {
                logger.LogInformation("Expired {Count} account export files.", expired);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError("Account export sweep failed: {ExceptionType}", e.GetType().Name);
        }
    }
}
