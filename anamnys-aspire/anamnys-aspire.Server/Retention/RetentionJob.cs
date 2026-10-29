using Anamnys.Server.Data;

namespace Anamnys.Server.Retention;

// Runs RetentionJobSteps at start (which also fills the clocks of records that predate F-06) and then once a day.
public sealed class RetentionJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<RetentionJob> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
                var created = await RetentionJobSteps.RunAsync(db, clock.GetUtcNow(), stoppingToken);
                logger.LogInformation("Retention run created {Count} notifications.", created);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // Type only: the exception message could carry row data.
                logger.LogError("Retention run failed: {ExceptionType}", e.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
