namespace Anamnys.Server.LiveSession;

public sealed class RecordingBufferSweeper(RecordingBuffers buffers, TimeProvider clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            buffers.SweepExpired();
        }
    }
}
