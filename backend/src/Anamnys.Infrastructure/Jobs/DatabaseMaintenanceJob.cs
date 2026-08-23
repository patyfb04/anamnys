using Anamnys.Infrastructure.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Anamnys.Infrastructure.Jobs;

/// <summary>
/// Calls the four scheduled maintenance routines defined in
/// <c>src/backend/db/03_jobs.sql</c>. This class is a CLOCK, not logic: every
/// rule lives in the database, because each of these exists precisely because a
/// constraint could not express it, and a rule enforced from application code is
/// a rule that the next caller forgets.
///
/// Scheduled from Program.cs. Do NOT use pg_cron on Neon: its jobs only fire
/// while the compute is awake, and a Neon compute scales to zero after
/// inactivity — which is exactly when the hold sweeper matters. It would fail
/// silently and look configured.
///
/// Each method returns the number of rows it touched, and LOGS IT. That number
/// is the only way to notice the job died: if <see cref="ReleaseExpiredHoldsAsync"/>
/// returns zero for days while people are booking, the sweeper is not running and
/// slots are quietly vanishing from every calendar. Nothing else raises an error.
/// </summary>
public class DatabaseMaintenanceJob
{
    private readonly AppDbContext _db;
    private readonly ILogger<DatabaseMaintenanceJob> _logger;

    public DatabaseMaintenanceJob(AppDbContext db, ILogger<DatabaseMaintenanceJob> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Safety net for expired booking holds. Hourly.
    ///
    /// The PRIMARY sweep belongs in the availability query (F-62): when free
    /// intervals are computed for a provider, release that provider's expired
    /// holds first. It cannot hang off the booking attempt instead — a hold that
    /// looks live removes the slot from the availability list, so the patient
    /// never sees it and never clicks it. The trigger would be an action the
    /// defect itself prevents.
    ///
    /// This routine stays after that is built, because if the availability query
    /// gets a bug or someone refactors the sweep away, nothing else notices.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 2)]
    public Task ReleaseExpiredHoldsAsync()
        => RunAsync("anamnys_release_expired_holds", "reservas vencidas liberadas");

    /// <summary>
    /// Expires recording consents past their validity window. Hourly.
    ///
    /// This one CANNOT move into a user flow, even though it looks like the same
    /// case as the sweeper. Recording capability would end up correct either way,
    /// but "ConsentEvents" would record the transition dated when someone
    /// happened to look, not when the consent actually lapsed. A consent trail
    /// dated by observation rather than by fact is exactly the record that fails
    /// under scrutiny, and being evidence is the whole point of that table.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    [AutomaticRetry(Attempts = 2)]
    public Task ExpireConsentsAsync()
        => RunAsync("anamnys_expire_consents", "consentimentos expirados");

    /// <summary>
    /// Deletes spent and long-expired portal tokens. Daily.
    /// A used single-use token is a standing credential until it is deleted.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 2)]
    public Task PurgeAuthTokensAsync()
        => RunAsync("anamnys_purge_auth_tokens", "tokens de portal purgados");

    /// <summary>
    /// Deletes abandoned booking holds after the retention window. Daily.
    /// Only abandoned ones: a hold that converted is provenance, and
    /// "Appointments"."HoldId" points back at it.
    ///
    /// LGPD art. 6 III (minimização): the purpose of a hold ends when it is
    /// released, and keeping identified booking attempts forever with no purpose
    /// attached is retention without basis.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 2)]
    public Task PurgeAbandonedHoldsAsync()
        => RunAsync("anamnys_purge_abandoned_holds", "reservas abandonadas purgadas");

    // The function name is a compile-time constant from the callers above, never
    // user input — but it is still interpolated into SQL, so it is validated
    // rather than trusted. A parameter cannot be used for an identifier.
    private async Task RunAsync(string function, string what)
    {
        if (!function.StartsWith("anamnys_", StringComparison.Ordinal)
            || !function.All(c => char.IsAsciiLetterLower(c) || c == '_'))
        {
            throw new ArgumentException($"Nome de função inválido: {function}", nameof(function));
        }

        var affected = await _db.Database
            .SqlQueryRaw<int>($"select {function}() as \"Value\"")
            .SingleAsync();

        // Always logged, including zero. Zero is information: for the hold
        // sweeper, a run of zeros while bookings are happening is the signal
        // that something upstream broke.
        _logger.LogInformation("Manutenção {Function}: {Affected} {What}.",
            function, affected, what);
    }
}
