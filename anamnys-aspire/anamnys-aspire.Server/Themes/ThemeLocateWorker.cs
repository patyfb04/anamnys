using Anamnys.Server.Data;

namespace Anamnys.Server.Themes;

// Runs the queued locates one at a time; the model itself is shared with suggestions under the
// suggester's gate, so a locate never runs alongside another suggestion. Failures are logged by note
// id and exception type only.
public sealed class ThemeLocateWorker(
    ThemeLocateQueue queue, IServiceScopeFactory scopes, IThemeSuggester suggester, TimeProvider clock,
    ILogger<ThemeLocateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (queue.Begin(job))
                {
                    await RunAsync(job, scopes, suggester, clock, logger, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal static async Task RunAsync(
        ThemeLocateJob job, IServiceScopeFactory scopes, IThemeSuggester suggester, TimeProvider clock, ILogger logger,
        CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            await NoteThemeService.LocateAsync(db, suggester, job.ProviderId, job.NoteId, clock.GetUtcNow(), logger, ct, job.TagId, job.SectionOnly);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogError("Background theme excerpt location failed for note {NoteId}: {ExceptionType}", job.NoteId, e.GetType().Name);
        }
    }
}
