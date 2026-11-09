using Anamnys.Server.Data;
using Anamnys.Server.Email;

namespace Anamnys.Server.Backup;

// Runs BackupMonitor at start and then every 15 minutes, only when Backup:Enabled (design/specs/2026-11-05-backup-restore-design.md
// §2.4), on the RetentionJob pattern. Logs the exception type only.
public sealed class BackupMonitorJob(
    IServiceScopeFactory scopes, BackupOptions options, TimeProvider clock, ILogger<BackupMonitorJob> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }
        // Start-up grace (BackupMonitor.StartupGrace): no "sem registro" alert before the agent's first runs.
        var startedAt = clock.GetUtcNow();
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
                var email = scope.ServiceProvider.GetService<IEmailSender>();
                var results = await BackupMonitor.CheckAsync(db, email, options, clock.GetUtcNow(), stoppingToken, startedAt: startedAt);
                logger.LogInformation("Backup check: {Failing} of {Total} checks failing.", results.Count(r => !r.Passed), results.Count);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Backup check failed: {ExceptionType}", e.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
