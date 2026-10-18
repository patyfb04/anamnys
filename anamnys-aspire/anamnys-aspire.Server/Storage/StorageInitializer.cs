namespace Anamnys.Server.Storage;

public sealed class StorageInitializer(S3NoteFileStore store, ILogger<StorageInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        const int attempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await store.EnsureBucketAsync(cancellationToken);
                break;
            }
            catch (Exception ex) when (attempt < attempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Note storage not ready (attempt {Attempt} of {Attempts}): {ErrorType}.", attempt, attempts, ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 5)), cancellationToken);
            }
        }

        logger.LogInformation("Note storage bucket ready.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
