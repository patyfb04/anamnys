using Anamnys.Server.Data;

namespace Anamnys.Server.LiveSession;

// Processing live notes left by a previous process lost their audio with it (spec §4.6).
public sealed class OrphanNoteSweep(IServiceScopeFactory scopes, TimeProvider clock, ILogger<OrphanNoteSweep> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
        var failed = await LiveRecordings.FailOrphansAsync(db, clock.GetUtcNow(), cancellationToken);
        if (failed > 0)
        {
            logger.LogWarning("Marked {Count} orphan live-session notes as failed.", failed);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
