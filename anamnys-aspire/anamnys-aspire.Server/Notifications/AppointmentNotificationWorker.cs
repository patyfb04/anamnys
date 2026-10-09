using Anamnys.Server.Data;
using Anamnys.Server.Email;

namespace Anamnys.Server.Notifications;

public sealed class AppointmentNotificationWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    IConfiguration configuration,
    ILogger<AppointmentNotificationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Appointment notification run failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();
        var now = clock.GetUtcNow();

        try
        {
            await NotificationWorkerSteps.CancelOverdueAsync(db, now, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // A failing cancel batch must never stop e-mails from being sent.
            logger.LogError(e, "Appointment auto-cancel step failed.");
        }

        var sender = scope.ServiceProvider.GetService<IEmailSender>();
        if (sender is null)
        {
            logger.LogInformation("No e-mail sender configured; appointment e-mails stay queued.");
            return;
        }
        if (configuration["PatientPortal:BaseUrl"] is not { Length: > 0 } baseUrl)
        {
            logger.LogError("PatientPortal:BaseUrl is not set; appointment e-mails stay queued.");
            return;
        }
        try
        {
            await NotificationWorkerSteps.SendDueAsync(db, sender, new Uri(baseUrl), now, ct, logger: logger);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError(e, "Appointment e-mail step failed.");
        }
    }
}
