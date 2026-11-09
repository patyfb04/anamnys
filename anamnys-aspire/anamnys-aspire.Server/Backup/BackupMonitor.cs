using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Backup;

public sealed record BackupCheckResult(string Check, bool Passed, TimeSpan? Age);

// What pg_stat_archiver says about WAL shipping. Null times: nothing archived (or failed) since the statistics reset.
public sealed record WalArchiverStatus(DateTimeOffset? LastArchivedAt, DateTimeOffset? LastFailedAt);

// Seam for the WAL check: tests run with archiving off, so they supply the values.
public interface IWalArchiverReader
{
    Task<WalArchiverStatus> ReadAsync(AnamnysDbContext db, CancellationToken ct);
}

// pg_stat_archiver is readable by every role in PostgreSQL, so the server's own database user reads it directly.
public sealed class PgStatArchiverReader : IWalArchiverReader
{
    public async Task<WalArchiverStatus> ReadAsync(AnamnysDbContext db, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQuery<ArchiverRow>($"""SELECT last_archived_time AS "LastArchivedAt", last_failed_time AS "LastFailedAt" FROM pg_stat_archiver""")
            .ToListAsync(ct);
        var row = rows.SingleOrDefault();
        return new WalArchiverStatus(row?.LastArchivedAt, row?.LastFailedAt);
    }

    private sealed class ArchiverRow
    {
        public DateTimeOffset? LastArchivedAt { get; set; }
        public DateTimeOffset? LastFailedAt { get; set; }
    }
}

// design/specs/2026-11-05-backup-restore-design.md §2.4. Four freshness checks; an alert is opened per check when it
// fails and closed when it passes again; one e-mail per episode. Nothing clinical goes in the e-mail: check name, age, time.
public static class BackupMonitor
{
    public const string Wal = "wal";
    public const string Full = "full";
    public const string Files = "files";
    public const string FailedRun = "failed_run";
    public const string Subject = "Anamnys: problema no backup";

    public static readonly TimeSpan WalMaxAge = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FullMaxAge = TimeSpan.FromHours(26);
    public static readonly TimeSpan FilesMaxAge = TimeSpan.FromMinutes(45);

    // At a fresh start the agent has not yet taken its first full or sync, and pg_stat_archiver may have nothing (first
    // deployment, first time backup is enabled, statistics reset by a crash). For this long after the server starts a
    // check with no record at all is not alerted; once that kind has a record it is judged normally.
    public static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(30);

    // email may be null (e-mail not configured): alerts are still recorded, just not sent. A failed send leaves
    // NotifiedAt empty, so the next check tries again. wal defaults to pg_stat_archiver. startedAt: when the server
    // started (null: no grace); a "no record" result inside the grace is returned as passing and leaves alerts as they are.
    public static async Task<IReadOnlyList<BackupCheckResult>> CheckAsync(
        AnamnysDbContext db, IEmailSender? email, BackupOptions options, DateTimeOffset now, CancellationToken ct,
        IWalArchiverReader? wal = null, DateTimeOffset? startedAt = null)
    {
        if (!options.Enabled)
        {
            return [];
        }

        var archiver = await (wal ?? new PgStatArchiverReader()).ReadAsync(db, ct);
        var results = new List<BackupCheckResult>
        {
            WalResult(archiver, now),
            await AgeResultAsync(db, Full, "full", FullMaxAge, now, ct),
            await AgeResultAsync(db, Files, "files", FilesMaxAge, now, ct),
            await FailedRunResultAsync(db, now, ct),
        };

        var inGrace = startedAt is { } started && now - started < StartupGrace;
        for (var i = 0; i < results.Count; i++)
        {
            if (inGrace && !results[i].Passed && results[i].Age is null && results[i].Check != FailedRun)
            {
                results[i] = results[i] with { Passed = true };
                continue;
            }
            await RecordAsync(db, email, options, results[i], now, ct);
        }
        return results;
    }

    private static BackupCheckResult WalResult(WalArchiverStatus status, DateTimeOffset now)
    {
        TimeSpan? age = status.LastArchivedAt is { } archived ? now - archived : null;
        var failing = status.LastFailedAt is { } failed && (status.LastArchivedAt is not { } a || failed > a);
        return new BackupCheckResult(Wal, age is { } fresh && fresh <= WalMaxAge && !failing, age);
    }

    private static async Task<BackupCheckResult> AgeResultAsync(
        AnamnysDbContext db, string check, string kind, TimeSpan maxAge, DateTimeOffset now, CancellationToken ct)
    {
        var last = await db.BackupRuns.AsNoTracking()
            .Where(r => r.Kind == kind && r.Outcome == "ok")
            .MaxAsync(r => (DateTimeOffset?)r.FinishedAt, ct);
        TimeSpan? age = last is { } finished ? now - finished : null;
        return new BackupCheckResult(check, age is { } a && a <= maxAge, age);
    }

    // A failed run is a problem when it is newer than the previous episode's close (a failure already resolved by a later
    // ok run still counts, once), and while an episode is open, as long as the newest run of some kind is still a failure.
    private static async Task<BackupCheckResult> FailedRunResultAsync(AnamnysDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var alerts = db.BackupAlerts.AsNoTracking().Where(a => a.Check == FailedRun);
        var open = await alerts.AnyAsync(a => a.ClosedAt == null, ct);
        var lastClosed = await alerts.MaxAsync(a => a.ClosedAt, ct);

        var runs = db.BackupRuns.AsNoTracking();
        var newestFailure = await runs.Where(r => r.Outcome == "failed").MaxAsync(r => (DateTimeOffset?)r.FinishedAt, ct);
        bool problem;
        if (open)
        {
            problem = false;
            foreach (var kind in await runs.Select(r => r.Kind).Distinct().ToListAsync(ct))
            {
                var newest = await runs.Where(r => r.Kind == kind).OrderByDescending(r => r.FinishedAt).Select(r => r.Outcome).FirstAsync(ct);
                problem |= newest == "failed";
            }
        }
        else
        {
            problem = newestFailure is { } failed && (lastClosed is not { } closed || failed > closed);
        }
        return new BackupCheckResult(FailedRun, !problem, problem && newestFailure is { } f ? now - f : null);
    }

    private static async Task RecordAsync(
        AnamnysDbContext db, IEmailSender? email, BackupOptions options, BackupCheckResult result, DateTimeOffset now, CancellationToken ct)
    {
        var alert = await db.BackupAlerts.FirstOrDefaultAsync(a => a.Check == result.Check && a.ClosedAt == null, ct);
        if (result.Passed)
        {
            if (alert is not null)
            {
                alert.ClosedAt = now;
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        if (alert is null)
        {
            alert = new BackupAlert { Id = Guid.NewGuid(), Check = result.Check, OpenedAt = now };
            db.BackupAlerts.Add(alert);
            await db.SaveChangesAsync(ct);
        }
        if (alert.NotifiedAt is not null || email is null || string.IsNullOrWhiteSpace(options.AlertEmail))
        {
            return;
        }
        try
        {
            await email.SendAsync(Message(options.AlertEmail, result, now), ct);
        }
        catch (EmailSendException)
        {
            return;
        }
        alert.NotifiedAt = now;
        await db.SaveChangesAsync(ct);
    }

    internal static EmailMessage Message(string to, BackupCheckResult result, DateTimeOffset now)
    {
        var age = result.Age is { } a ? $"{(int)a.TotalMinutes} minutos" : "sem registro";
        var time = $"{now.ToUniversalTime():yyyy-MM-dd HH:mm} UTC";
        var text = $"Verificação: {result.Check}\nIdade: {age}\nHorário: {time}";
        var html = $"<p>Verificação: {result.Check}<br>Idade: {age}<br>Horário: {time}</p>";
        return new EmailMessage(to, "Administrador", Subject, text, html);
    }
}
