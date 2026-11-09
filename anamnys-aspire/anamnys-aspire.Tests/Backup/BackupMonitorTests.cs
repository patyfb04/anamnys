using Anamnys.Server.Backup;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Tests.Notifications;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Backup;

// design/specs/2026-11-05-backup-restore-design.md §2.4, §6. The WAL values are supplied by the test: archiving is off in
// the test PostgreSQL, so pg_stat_archiver has nothing to say.
[Collection(SharedAppHostCollection.Name)]
public class BackupMonitorTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);
    private static readonly BackupOptions On = new() { Enabled = true, AlertEmail = "admin@test.local" };

    private sealed class FakeWal(WalArchiverStatus status) : IWalArchiverReader
    {
        public Task<WalArchiverStatus> ReadAsync(AnamnysDbContext db, CancellationToken ct) => Task.FromResult(status);
    }

    private static FakeWal FreshWal(DateTimeOffset now) => new(new WalArchiverStatus(now.AddMinutes(-1), null));

    private static async Task AddRunAsync(AnamnysDbContext db, string kind, string outcome, DateTimeOffset finishedAt)
    {
        db.BackupRuns.Add(new BackupRun
        {
            Id = Guid.NewGuid(), Kind = kind, Outcome = outcome, StartedAt = finishedAt.AddMinutes(-2), FinishedAt = finishedAt,
            ErrorCode = outcome == "failed" ? "push_failed" : null,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task AddHealthyRunsAsync(AnamnysDbContext db, DateTimeOffset now)
    {
        await AddRunAsync(db, "full", "ok", now.AddHours(-3));
        await AddRunAsync(db, "files", "ok", now.AddMinutes(-5));
    }

    private static BackupCheckResult Of(IReadOnlyList<BackupCheckResult> results, string check) => results.Single(r => r.Check == check);

    [Fact]
    public async Task CheckAsync_Disabled_DoesNothing()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        var email = new RecordingEmailSender();

        // Act
        var results = await BackupMonitor.CheckAsync(db, email, new BackupOptions { Enabled = false, AlertEmail = "admin@test.local" }, Now, Ct, new FakeWal(new WalArchiverStatus(null, null)));

        // Assert
        results.Should().BeEmpty();
        email.Sent.Should().BeEmpty();
        (await s.CountAsync("""SELECT count(*) FROM "BackupAlerts" """, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_EverythingFresh_NoAlertNoEmail()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddHealthyRunsAsync(db, Now);
        var email = new RecordingEmailSender();

        // Act
        var results = await BackupMonitor.CheckAsync(db, email, On, Now, Ct, FreshWal(Now));

        // Assert
        results.Select(r => r.Check).Should().Equal("wal", "full", "files", "failed_run");
        results.Should().OnlyContain(r => r.Passed);
        Of(results, "wal").Age.Should().Be(TimeSpan.FromMinutes(1));
        Of(results, "full").Age.Should().Be(TimeSpan.FromHours(3));
        Of(results, "files").Age.Should().Be(TimeSpan.FromMinutes(5));
        email.Sent.Should().BeEmpty();
        (await s.CountAsync("""SELECT count(*) FROM "BackupAlerts" """, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_Boundaries_AreInclusive()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddRunAsync(db, "full", "ok", Now.AddHours(-26));
        await AddRunAsync(db, "files", "ok", Now.AddMinutes(-45));

        // Act
        var results = await BackupMonitor.CheckAsync(db, new RecordingEmailSender(), On, Now, Ct, new FakeWal(new WalArchiverStatus(Now.AddMinutes(-10), null)));

        // Assert
        results.Should().OnlyContain(r => r.Passed);
    }

    [Fact]
    public async Task CheckAsync_WalOlderThanTenMinutes_AlertsWal()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddHealthyRunsAsync(db, Now);
        var email = new RecordingEmailSender();

        // Act
        var results = await BackupMonitor.CheckAsync(db, email, On, Now, Ct, new FakeWal(new WalArchiverStatus(Now.AddMinutes(-11), null)));

        // Assert
        results.Where(r => !r.Passed).Select(r => r.Check).Should().Equal("wal");
        Of(results, "wal").Age.Should().Be(TimeSpan.FromMinutes(11));
        email.Sent.Should().ContainSingle().Which.Text.Should().Contain("wal");
    }

    [Fact]
    public async Task CheckAsync_WalFailureNewerThanLastArchive_AlertsWal()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddHealthyRunsAsync(db, Now);
        var email = new RecordingEmailSender();

        // Act
        var results = await BackupMonitor.CheckAsync(db, email, On, Now, Ct, new FakeWal(new WalArchiverStatus(Now.AddMinutes(-3), Now.AddMinutes(-1))));

        // Assert
        Of(results, "wal").Passed.Should().BeFalse();
        email.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task CheckAsync_WalFailureOlderThanLastArchive_Passes()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddHealthyRunsAsync(db, Now);

        // Act
        var results = await BackupMonitor.CheckAsync(db, new RecordingEmailSender(), On, Now, Ct, new FakeWal(new WalArchiverStatus(Now.AddMinutes(-1), Now.AddMinutes(-30))));

        // Assert
        Of(results, "wal").Passed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_NothingArchivedYet_AlertsWalWithoutAge()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddHealthyRunsAsync(db, Now);

        // Act
        var results = await BackupMonitor.CheckAsync(db, new RecordingEmailSender(), On, Now, Ct, new FakeWal(new WalArchiverStatus(null, null)));

        // Assert
        Of(results, "wal").Should().Be(new BackupCheckResult("wal", false, null));
    }

    [Fact]
    public async Task CheckAsync_FullOlderThan26Hours_AlertsFull()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddRunAsync(db, "full", "ok", Now.AddHours(-27));
        await AddRunAsync(db, "files", "ok", Now.AddMinutes(-5));
        var email = new RecordingEmailSender();

        // Act
        var results = await BackupMonitor.CheckAsync(db, email, On, Now, Ct, FreshWal(Now));

        // Assert
        results.Where(r => !r.Passed).Select(r => r.Check).Should().Equal("full");
        Of(results, "full").Age.Should().Be(TimeSpan.FromHours(27));
        email.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task CheckAsync_FullFailedRunDoesNotCountAsFresh()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddRunAsync(db, "full", "ok", Now.AddHours(-30));
        await AddRunAsync(db, "full", "failed", Now.AddHours(-1));
        await AddRunAsync(db, "files", "ok", Now.AddMinutes(-5));

        // Act
        var results = await BackupMonitor.CheckAsync(db, new RecordingEmailSender(), On, Now, Ct, FreshWal(Now));

        // Assert
        Of(results, "full").Passed.Should().BeFalse();
        Of(results, "failed_run").Passed.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_FilesOlderThan45Minutes_AlertsFiles()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddRunAsync(db, "full", "ok", Now.AddHours(-3));
        await AddRunAsync(db, "files", "ok", Now.AddMinutes(-46));
        var email = new RecordingEmailSender();

        // Act
        var results = await BackupMonitor.CheckAsync(db, email, On, Now, Ct, FreshWal(Now));

        // Assert
        results.Where(r => !r.Passed).Select(r => r.Check).Should().Equal("files");
        email.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task CheckAsync_NoRunsAtAll_AlertsFullAndFilesWithoutAge()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();

        // Act
        var results = await BackupMonitor.CheckAsync(db, new RecordingEmailSender(), On, Now, Ct, FreshWal(Now));

        // Assert
        Of(results, "full").Should().Be(new BackupCheckResult("full", false, null));
        Of(results, "files").Should().Be(new BackupCheckResult("files", false, null));
        Of(results, "failed_run").Passed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_FailedRun_AlertsFailedRunEvenIfALaterRunSucceeded_ThenClears()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddHealthyRunsAsync(db, Now);
        await AddRunAsync(db, "files", "failed", Now.AddMinutes(-12));
        await AddRunAsync(db, "files", "ok", Now.AddMinutes(-2));
        var email = new RecordingEmailSender();

        // Act
        var first = await BackupMonitor.CheckAsync(db, email, On, Now, Ct, FreshWal(Now));
        var second = await BackupMonitor.CheckAsync(db, email, On, Now.AddMinutes(15), Ct, FreshWal(Now.AddMinutes(15)));
        await AddRunAsync(db, "files", "ok", Now.AddMinutes(14));
        var third = await BackupMonitor.CheckAsync(db, email, On, Now.AddMinutes(30), Ct, FreshWal(Now.AddMinutes(30)));

        // Assert
        Of(first, "failed_run").Passed.Should().BeFalse();
        Of(second, "failed_run").Passed.Should().BeTrue();
        Of(third, "failed_run").Passed.Should().BeTrue();
        email.Sent.Should().ContainSingle();
        (await s.CountAsync("""SELECT count(*) FROM "BackupAlerts" WHERE "Check" = 'failed_run' AND "ClosedAt" IS NOT NULL""", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_FailedRunStillTheNewestOfItsKind_KeepsTheEpisodeOpen()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddRunAsync(db, "full", "ok", Now.AddHours(-3));
        await AddRunAsync(db, "files", "failed", Now.AddMinutes(-2));
        var email = new RecordingEmailSender();

        // Act
        var first = await BackupMonitor.CheckAsync(db, email, On, Now, Ct, FreshWal(Now));
        var second = await BackupMonitor.CheckAsync(db, email, On, Now.AddMinutes(15), Ct, FreshWal(Now.AddMinutes(15)));

        // Assert
        Of(first, "failed_run").Passed.Should().BeFalse();
        Of(second, "failed_run").Passed.Should().BeFalse();
        email.Sent.Where(m => m.Text.Contains("failed_run")).Should().ContainSingle();
    }

    [Fact]
    public async Task CheckAsync_SameEpisode_OneEmailPerCheck_ReArmedAfterRecovery()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddHealthyRunsAsync(db, Now);
        var stale = new FakeWal(new WalArchiverStatus(Now.AddMinutes(-30), null));
        var email = new RecordingEmailSender();

        // Act
        await BackupMonitor.CheckAsync(db, email, On, Now, Ct, stale);
        await BackupMonitor.CheckAsync(db, email, On, Now.AddMinutes(1), Ct, stale);
        var afterTwo = email.Sent.Count;
        await BackupMonitor.CheckAsync(db, email, On, Now.AddMinutes(2), Ct, FreshWal(Now.AddMinutes(2)));
        var recovered = await s.CountAsync("""SELECT count(*) FROM "BackupAlerts" WHERE "Check" = 'wal' AND "ClosedAt" IS NOT NULL""", Ct);
        await BackupMonitor.CheckAsync(db, email, On, Now.AddMinutes(60), Ct, new FakeWal(new WalArchiverStatus(Now.AddMinutes(10), null)));

        // Assert
        afterTwo.Should().Be(1);
        recovered.Should().Be(1);
        email.Sent.Count(m => m.Text.Contains("Verificação: wal")).Should().Be(2);
        (await s.CountAsync("""SELECT count(*) FROM "BackupAlerts" WHERE "Check" = 'wal' AND "ClosedAt" IS NULL""", Ct)).Should().Be(1);
        (await s.CountAsync("""SELECT count(*) FROM "BackupAlerts" WHERE "Check" = 'wal' AND "NotifiedAt" IS NOT NULL""", Ct)).Should().Be(2);
    }

    [Fact]
    public async Task CheckAsync_SendFails_NotifiedAtStaysEmpty_RetriedNextCheck()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddHealthyRunsAsync(db, Now);
        var stale = new FakeWal(new WalArchiverStatus(Now.AddMinutes(-30), null));
        var email = new RecordingEmailSender { FailNext = 1 };

        // Act
        await BackupMonitor.CheckAsync(db, email, On, Now, Ct, stale);
        var notifiedAfterFailure = await s.CountAsync("""SELECT count(*) FROM "BackupAlerts" WHERE "NotifiedAt" IS NOT NULL""", Ct);
        await BackupMonitor.CheckAsync(db, email, On, Now.AddMinutes(15), Ct, stale);

        // Assert
        notifiedAfterFailure.Should().Be(0);
        email.Sent.Should().ContainSingle();
        (await s.CountAsync("""SELECT count(*) FROM "BackupAlerts" WHERE "NotifiedAt" IS NOT NULL""", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_NoSenderConfigured_StillRecordsTheAlert()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddHealthyRunsAsync(db, Now);

        // Act
        await BackupMonitor.CheckAsync(db, null, On, Now, Ct, new FakeWal(new WalArchiverStatus(null, null)));

        // Assert
        (await s.CountAsync("""SELECT count(*) FROM "BackupAlerts" WHERE "ClosedAt" IS NULL AND "NotifiedAt" IS NULL""", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_Email_HasSubjectCheckAgeAndTime_AndNothingClinical()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Mariana", Ct);
        await s.AddNoteAsync(provider, patient, Now.AddDays(-1), Ct, signedAt: Now.AddDays(-1));
        await using var db = s.CreateDbContext();
        await AddRunAsync(db, "full", "ok", Now.AddHours(-3));
        await AddRunAsync(db, "files", "ok", Now.AddMinutes(-60));
        var email = new RecordingEmailSender();

        // Act
        await BackupMonitor.CheckAsync(db, email, On, Now, Ct, FreshWal(Now));

        // Assert
        var message = email.Sent.Should().ContainSingle().Subject;
        message.ToEmail.Should().Be("admin@test.local");
        message.Subject.Should().Be("Anamnys: problema no backup");
        message.Text.Should().Contain("files").And.Contain("60 minutos").And.Contain("2026-11-05 15:00");
        (message.Text + message.Html).Should().NotContain("Mariana").And.NotContain("Silva").And.NotContain("Dra. Teste");
    }

    [Fact]
    public async Task CheckAsync_NoRecordWithinStartupGrace_NoAlertNoEmail()
    {
        // Arrange: a fresh start; the agent has not taken a full or a sync yet and nothing was archived.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        var email = new RecordingEmailSender();

        // Act
        var results = await BackupMonitor.CheckAsync(db, email, On, Now, Ct, new FakeWal(new WalArchiverStatus(null, null)), startedAt: Now.AddMinutes(-29));

        // Assert
        results.Should().OnlyContain(r => r.Passed);
        email.Sent.Should().BeEmpty();
        (await s.CountAsync("""SELECT count(*) FROM "BackupAlerts" """, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_NoRecordAfterStartupGrace_Alerts()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        var email = new RecordingEmailSender();

        // Act: 30 minutes after the start the grace is over.
        var results = await BackupMonitor.CheckAsync(db, email, On, Now, Ct, new FakeWal(new WalArchiverStatus(null, null)), startedAt: Now.AddMinutes(-30));

        // Assert
        results.Where(r => !r.Passed).Select(r => r.Check).Should().Equal("wal", "full", "files");
        email.Sent.Should().HaveCount(3);
    }

    [Fact]
    public async Task CheckAsync_StaleRecordWithinStartupGrace_StillAlerts()
    {
        // Arrange: the grace covers only checks with no record; an old full is a real problem.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        await using var db = s.CreateDbContext();
        await AddRunAsync(db, "full", "ok", Now.AddHours(-27));
        var email = new RecordingEmailSender();

        // Act
        var results = await BackupMonitor.CheckAsync(db, email, On, Now, Ct, FreshWal(Now), startedAt: Now.AddMinutes(-5));

        // Assert
        Of(results, "full").Passed.Should().BeFalse();
        Of(results, "files").Passed.Should().BeTrue();
        email.Sent.Should().ContainSingle().Which.Text.Should().Contain("full");
    }
}
