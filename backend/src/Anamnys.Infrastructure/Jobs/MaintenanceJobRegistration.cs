using Anamnys.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// NOTE: this file takes IServiceProvider, not WebApplication, on purpose.
// Anamnys.Infrastructure is a plain class library (Microsoft.NET.Sdk) with no
// FrameworkReference to Microsoft.AspNetCore.App — WebApplication is not
// available here, and should not be: the infrastructure layer has no business
// knowing there is a web host in front of it.

namespace Anamnys.Infrastructure.Jobs;

/// <summary>
/// Registers the four recurring maintenance jobs, and refuses to pretend they
/// are scheduled when the routines they call do not exist.
/// </summary>
public static class MaintenanceJobRegistration
{
    // The four routines defined in src/backend/db/03_jobs.sql.
    private static readonly string[] RequiredFunctions =
    [
        "anamnys_release_expired_holds",
        "anamnys_expire_consents",
        "anamnys_purge_auth_tokens",
        "anamnys_purge_abandoned_holds",
    ];

    /// <summary>
    /// Call once, after the app is built.
    ///
    /// CRON TIMES ARE UTC and deliberately ALIGNED rather than staggered. The
    /// usual instinct is to spread jobs out to smooth load; on a compute that
    /// scales to zero that instinct is backwards. Two hourly jobs ten minutes
    /// apart wake the Neon compute TWICE an hour and bill for both windows; the
    /// same two at the same minute wake it once. Load is not the constraint
    /// here — wake-ups are.
    ///
    /// 03:00 UTC is midnight in Brazil (UTC-3), which is the quietest hour for
    /// the daily purges.
    /// </summary>
    public static async Task AddDatabaseMaintenanceJobsAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(MaintenanceJobRegistration));

        // Same guard as the top of 03_jobs.sql, for the same reason. PostgreSQL
        // does not validate a plpgsql body until the function is CALLED, so a
        // database where 03_jobs.sql never ran looks fine until the first hourly
        // run fails inside a background worker, where nobody is watching.
        // Better to say so once, loudly, at startup.
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var missing = await FindMissingFunctionsAsync(db);

        if (missing.Count > 0)
        {
            logger.LogError(
                "Rotinas de manutenção AUSENTES no banco: {Missing}. " +
                "Os jobs recorrentes NÃO foram agendados. Rodar src/backend/db/03_jobs.sql. " +
                "Sem elas, reservas vencidas continuam bloqueando horários e consentimentos " +
                "vencidos continuam permitindo gravação.",
                string.Join(", ", missing));
            return;
        }

        var jobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

        jobs.AddOrUpdate<DatabaseMaintenanceJob>(
            "release-expired-holds",
            j => j.ReleaseExpiredHoldsAsync(),
            "0 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        jobs.AddOrUpdate<DatabaseMaintenanceJob>(
            "expire-consents",
            j => j.ExpireConsentsAsync(),
            "0 * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        jobs.AddOrUpdate<DatabaseMaintenanceJob>(
            "purge-auth-tokens",
            j => j.PurgeAuthTokensAsync(),
            "0 3 * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        jobs.AddOrUpdate<DatabaseMaintenanceJob>(
            "purge-abandoned-holds",
            j => j.PurgeAbandonedHoldsAsync(),
            "0 3 * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        logger.LogInformation(
            "Manutenção agendada: 2 rotinas horárias (00 UTC) e 2 diárias (03:00 UTC).");
    }

    private static async Task<List<string>> FindMissingFunctionsAsync(AppDbContext db)
    {
        var present = await db.Database
            .SqlQueryRaw<string>(
                """
                select p.proname as "Value"
                  from pg_proc p
                  join pg_namespace n on n.oid = p.pronamespace
                 where n.nspname = 'public'
                   and p.proname like 'anamnys%'
                """)
            .ToListAsync();

        return RequiredFunctions.Except(present).ToList();
    }
}
