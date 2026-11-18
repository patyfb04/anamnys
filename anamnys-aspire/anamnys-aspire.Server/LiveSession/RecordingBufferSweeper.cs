using Anamnys.Server.Data;

namespace Anamnys.Server.LiveSession;

// Drops idle buffers (zeroed under the buffers' lock) and records each one as abandoned (F-26) outside that lock.
public sealed class RecordingBufferSweeper(
    RecordingBuffers buffers, IServiceScopeFactory scopes, TimeProvider clock, ILogger<RecordingBufferSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError("Recording buffer sweep failed: {ExceptionType}", e.GetType().Name);
            }
        }
    }

    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var swept = buffers.SweepExpired();
        if (swept.Count == 0)
        {
            return 0;
        }
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
        foreach (var (recordingId, meta) in swept)
        {
            await AudioDestruction.TryRecordAsync(db, recordingId, meta, null, AudioDestruction.Abandoned, clock.GetUtcNow(), logger, ct);
        }
        return swept.Count;
    }
}
