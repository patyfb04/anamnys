using Anamnys.Server.Data;
using Anamnys.Server.NoteStructuring;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.Themes;

// Loads the model and builds the suggestion prefix cache once the app is up, and again whenever the
// vocabulary changes (ThemeWarmupSignal), so no provider waits for it in the suggest dialog.
public sealed class ThemeWarmupService(
    IServiceScopeFactory scopes, IThemeSuggester suggester, ThemeWarmupSignal signal,
    IHostApplicationLifetime lifetime, IOptions<NoteStructuringOptions> options, ILogger<ThemeWarmupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WarmSuggestionOnStart)
        {
            return;
        }
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var onStarted = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            using var onStopping = stoppingToken.Register(() => started.TrySetCanceled());
            await started.Task;

            signal.Request();
            while (!stoppingToken.IsCancellationRequested)
            {
                await signal.WaitAsync(stoppingToken);
                await WarmOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task WarmOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
            var terms = await ThemeVocabularyQuery.ActiveTermOptionsAsync(db, ct);
            await suggester.WarmAsync(terms, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Type only: messages may carry paths or data. The next suggestion builds the cache lazily.
            logger.LogWarning("Theme suggestion warm-up failed ({ExceptionType}).", ex.GetType().Name);
        }
    }
}
