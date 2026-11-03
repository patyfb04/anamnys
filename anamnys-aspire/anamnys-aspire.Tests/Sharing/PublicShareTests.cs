using System.Net;
using System.Net.Http.Json;
using Anamnys.Server.Sharing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;

namespace Anamnys.Tests.Sharing;

// The recipient's side (design/specs/2026-11-01-protected-sharing-design.md §3, §6): the service for the races, real HTTP
// for what the recipient sees.
[Collection(SharedAppHostCollection.Name)]
public class PublicShareTests(SharedAppHostFixture fixture)
{
    private static DateTimeOffset Now => ShareScratch.Now;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static PublicShareTests() => QuestPDF.Settings.License = LicenseType.Community;

    private Task<ShareScratch> StartAsync() => ShareScratch.CreateAsync(fixture, Ct);

    private static Task<PublicShareHost> HostAsync(ShareScratch s) => PublicShareHost.StartAsync(s.Db, new FakeTimeProvider(Now), Ct);

    private static Task<HttpResponseMessage> OpenAsync(PublicShareHost host, string token, string password) =>
        host.Client.PostAsJsonAsync("/api/public/shares/open", new { token, password }, Ct);

    private static Task<HttpResponseMessage> CheckAsync(PublicShareHost host, string token) =>
        host.Client.PostAsJsonAsync("/api/public/shares/check", new { token }, Ct);

    private static async Task<string> SnapshotAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key != "Date")
            .OrderBy(h => h.Key)
            .Select(h => $"{h.Key}={string.Join(',', h.Value)}");
        return $"{(int)response.StatusCode}|{string.Join(';', headers)}|{await response.Content.ReadAsStringAsync(Ct)}";
    }

    [Fact]
    public async Task Open_RightPassword_ServesTheWatermarkedPdf_NoStoreNoindexInline_AndLogsTheOpening()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct, cap: 2, recipient: "Dra. Clara Nunes");
        await using var host = await HostAsync(s);

        // Act
        using var response = await OpenAsync(host, link.Token, link.Password);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("X-Robots-Tag").Should().Equal("noindex");
        response.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");
        response.Content.Headers.ContentDisposition.FileName.Should().BeNull();
        using var pdf = PdfDocument.Open(await response.Content.ReadAsByteArrayAsync(Ct));
        pdf.GetPages().Should().OnlyContain(p => p.Text.Replace(" ", "").Contains("Dra.ClaraNunes") && p.Text.Replace(" ", "").Contains("01/11/202612:00"));
        await using var db = s.Db.CreateDbContext();
        (await db.ShareLinks.AsNoTracking().SingleAsync(Ct)).ViewsUsed.Should().Be(1);
        var access = await db.ShareAccesses.AsNoTracking().SingleAsync(Ct);
        access.Outcome.Should().Be("opened");
        s.Store.Puts.Should().Be(0);
    }

    [Fact]
    public async Task Open_DownloadEnabled_IsAnAttachment()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct, download: true);
        await using var host = await HostAsync(s);

        // Act
        using var response = await OpenAsync(host, link.Token, link.Password);

        // Assert
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName.Should().StartWith("documento-");
    }

    [Fact]
    public async Task Open_EmptyPassword_AnswersLikeAnyOtherFailure()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);
        await using var host = await HostAsync(s);

        // Act
        using var live = await OpenAsync(host, link.Token, "");
        using var unknown = await OpenAsync(host, ShareSecrets.NewToken(), "");

        // Assert
        live.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Check_LiveLink_SaysOnlyThatItIsAvailable()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);
        await using var host = await HostAsync(s);

        // Act
        using var response = await CheckAsync(host, link.Token);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be("""{"available":true}""");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("X-Robots-Tag").Should().Equal("noindex");
    }

    [Fact]
    public async Task UnknownExpiredRevokedCappedAndBlocked_AnswerExactlyTheSame_OnCheckAndOnOpen()
    {
        // Arrange
        await using var s = await StartAsync();
        var expired = await s.SharedAsync(Ct, at: Now.AddDays(-8));
        var revoked = await s.SharedAsync(Ct);
        var capped = await s.SharedAsync(Ct, cap: 1);
        var blocked = await s.SharedAsync(Ct);
        await using (var db = s.Db.CreateDbContext())
        {
            await ShareService.RevokeAsync(db, s.Provider, revoked.Id, Now, Ct);
        }
        (await s.OpenAsync(capped.Token, capped.Password, Ct)).Outcome.Should().Be(PublicOpenOutcome.Opened);
        for (var i = 0; i < 5; i++)
        {
            await s.OpenAsync(blocked.Token, "errada", Ct);
        }
        await using var host = await HostAsync(s);
        var links = new[]
        {
            (Token: ShareSecrets.NewToken(), Password: "qualquer"), (expired.Token, expired.Password), (revoked.Token, revoked.Password),
            (capped.Token, capped.Password), (blocked.Token, blocked.Password),
        };

        // Act
        var answers = new List<string>();
        foreach (var (token, password) in links)
        {
            using var check = await CheckAsync(host, token);
            using var open = await OpenAsync(host, token, password);
            answers.Add(await SnapshotAsync(check));
            answers.Add(await SnapshotAsync(open));
        }

        // Assert
        answers.Distinct().Should().ContainSingle().Which.Should().StartWith("404|").And.Contain("share_unavailable").And.Contain("no-store");
    }

    [Fact]
    public async Task Open_FiveWrongPasswords_BlocksTheLink_EvenForTheRightPassword()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);
        await using var host = await HostAsync(s);

        // Act
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var wrong = await OpenAsync(host, link.Token, "errada");
            statuses.Add(wrong.StatusCode);
        }
        using var right = await OpenAsync(host, link.Token, link.Password);

        // Assert
        statuses.Should().OnlyContain(c => c == HttpStatusCode.Forbidden);
        right.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = s.Db.CreateDbContext();
        var row = await db.ShareLinks.AsNoTracking().SingleAsync(Ct);
        row.WrongPasswordCount.Should().Be(5);
        row.BlockedAt.Should().Be(Now);
        var outcomes = await db.ShareAccesses.AsNoTracking().Select(a => a.Outcome).ToListAsync(Ct);
        outcomes.Count(o => o == "bad_password").Should().Be(5);
        outcomes.Should().Contain("blocked").And.NotContain("opened");
    }

    [Fact]
    public async Task OpenAsync_ConcurrentWrongPasswords_NeverPassTheFifth()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => s.OpenAsync(link.Token, "errada", Ct)));

        // Assert
        results.Count(r => r.Outcome == PublicOpenOutcome.WrongPassword).Should().Be(5);
        results.Count(r => r.Outcome == PublicOpenOutcome.Unavailable).Should().Be(7);
        await using var db = s.Db.CreateDbContext();
        (await db.ShareLinks.AsNoTracking().SingleAsync(Ct)).WrongPasswordCount.Should().Be(5);
    }

    [Fact]
    public async Task OpenAsync_ConcurrentOpens_NeverPassTheViewCap()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct, cap: 3);

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => s.OpenAsync(link.Token, link.Password, Ct)));

        // Assert
        results.Count(r => r.Outcome == PublicOpenOutcome.Opened).Should().Be(3);
        results.Count(r => r.Outcome == PublicOpenOutcome.Unavailable).Should().Be(7);
        await using var db = s.Db.CreateDbContext();
        (await db.ShareLinks.AsNoTracking().SingleAsync(Ct)).ViewsUsed.Should().Be(3);
    }

    [Fact]
    public async Task OpenAsync_AtTheExpiryInstant_IsUnavailable()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);

        // Act
        var before = await s.OpenAsync(link.Token, link.Password, Ct, link.ExpiresAt.AddSeconds(-1));
        var at = await s.OpenAsync(link.Token, link.Password, Ct, link.ExpiresAt);

        // Assert
        before.Outcome.Should().Be(PublicOpenOutcome.Opened);
        at.Outcome.Should().Be(PublicOpenOutcome.Unavailable);
    }

    [Fact]
    public async Task Open_PastTheLimitOfAttemptsPerMinute_Is429()
    {
        // Arrange
        await using var s = await StartAsync();
        await using var host = await HostAsync(s);

        // Act
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < ShareEndpoints.PasswordAttemptsPerMinute + 1; i++)
        {
            using var response = await OpenAsync(host, ShareSecrets.NewToken(), "qualquer");
            statuses.Add(response.StatusCode);
        }

        // Assert
        statuses.Take(ShareEndpoints.PasswordAttemptsPerMinute).Should().OnlyContain(c => c == HttpStatusCode.NotFound);
        statuses[^1].Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Open_NoSlotFree_Is429Busy_BeforeTouchingTheLink()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);
        var limiter = new ShareAttemptLimiter(1, 0);
        using var held = await limiter.AcquireAsync(Ct);
        await using var host = await PublicShareHost.StartAsync(s.Db, new FakeTimeProvider(Now), Ct, limiter);

        // Act
        using var response = await OpenAsync(host, link.Token, "errada");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.GetValues("Retry-After").Should().Equal("5");
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("\"code\":\"busy\"");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        await using var db = s.Db.CreateDbContext();
        (await db.ShareLinks.AsNoTracking().SingleAsync(Ct)).WrongPasswordCount.Should().Be(0);
        (await db.ShareAccesses.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task OpenAsync_WaitingOnARowLock_KeepsItsSlot_SoTheNextAttemptIsBusy_AndNotAnotherConnection()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);
        s.Limiter = new ShareAttemptLimiter(1, 0);
        await using var holder = await s.Db.OpenAsync(Ct);
        await using var lockTx = await holder.BeginTransactionAsync(Ct);
        await using (var hold = new Npgsql.NpgsqlCommand("""SELECT 1 FROM "ShareLinks" FOR UPDATE""", holder, lockTx))
        {
            await hold.ExecuteNonQueryAsync(Ct);
        }
        var first = s.OpenAsync(link.Token, link.Password, Ct);
        await WaitForLockWaiterAsync(s);

        // Act
        var second = await s.OpenAsync(link.Token, link.Password, Ct);
        await lockTx.RollbackAsync(Ct);

        // Assert
        second.Outcome.Should().Be(PublicOpenOutcome.Busy);
        (await first).Outcome.Should().Be(PublicOpenOutcome.Opened);
    }

    [Fact]
    public async Task OpenAsync_CancelledWhileQueued_ThrowsAndCountsNothing()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);
        s.Limiter = new ShareAttemptLimiter(1, 1);
        var held = await s.Limiter.AcquireAsync(Ct);
        using var cancel = new CancellationTokenSource();

        // Act
        var queued = s.OpenAsync(link.Token, "errada", cancel.Token);
        await cancel.CancelAsync();
        var act = async () => await queued;

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        held.Dispose();
        await using var db = s.Db.CreateDbContext();
        (await db.ShareLinks.AsNoTracking().SingleAsync(Ct)).WrongPasswordCount.Should().Be(0);
    }

    [Fact]
    public async Task OpenAsync_LinkExpiresWhileWaitingForTheLock_IsUnavailable()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);
        var clock = new FakeTimeProvider(Now);
        await using var holder = await s.Db.OpenAsync(Ct);
        await using var lockTx = await holder.BeginTransactionAsync(Ct);
        await using (var hold = new Npgsql.NpgsqlCommand("""SELECT 1 FROM "ShareLinks" FOR UPDATE""", holder, lockTx))
        {
            await hold.ExecuteNonQueryAsync(Ct);
        }
        await using var db = s.Db.CreateDbContext();

        // Act
        var waiting = PublicShareService.OpenAsync(db, s.Limiter, clock, link.Token, link.Password, null, Ct);
        await WaitForLockWaiterAsync(s);
        clock.SetUtcNow(link.ExpiresAt);
        await lockTx.RollbackAsync(Ct);
        var result = await waiting;

        // Assert
        result.Outcome.Should().Be(PublicOpenOutcome.Unavailable);
        await using var check = s.Db.CreateDbContext();
        (await check.ShareLinks.AsNoTracking().SingleAsync(Ct)).ViewsUsed.Should().Be(0);
    }

    [Fact]
    public async Task OpenAsync_RepeatsOnADeadLink_LogOncePerLinkPerMinute()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);
        await using (var db = s.Db.CreateDbContext())
        {
            await ShareService.RevokeAsync(db, s.Provider, link.Id, Now, Ct);
        }

        // Act
        await s.OpenAsync(link.Token, link.Password, Ct, Now);
        await s.OpenAsync(link.Token, link.Password, Ct, Now.AddSeconds(30));
        await s.OpenAsync(link.Token, link.Password, Ct, Now.AddSeconds(59));
        await s.OpenAsync(link.Token, link.Password, Ct, Now.AddSeconds(61));

        // Assert
        await using var check = s.Db.CreateDbContext();
        (await check.ShareAccesses.CountAsync(a => a.Outcome == "revoked", Ct)).Should().Be(2);
    }

    [Fact]
    public async Task PublicRoutes_PastTheLimitForAllClients_Are429()
    {
        // Arrange
        await using var s = await StartAsync();
        await using var host = await PublicShareHost.StartAsync(s.Db, new FakeTimeProvider(Now), Ct, allClientsPerMinute: 3);

        // Act
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await CheckAsync(host, ShareSecrets.NewToken());
            statuses.Add(response.StatusCode);
        }

        // Assert
        statuses.Take(3).Should().OnlyContain(c => c == HttpStatusCode.NotFound);
        statuses[3].Should().Be(HttpStatusCode.TooManyRequests);
    }

    // Until another session is blocked on a row lock in this scratch database.
    private static async Task WaitForLockWaiterAsync(ShareScratch s)
    {
        for (var i = 0; i < 400; i++)
        {
            if (await s.Db.CountAsync(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", Ct) > 0)
            {
                return;
            }
            await Task.Delay(25, Ct);
        }
        throw new TimeoutException("No session started waiting for the row lock.");
    }

    [Fact]
    public async Task PublicRoutes_LeaveTokenPasswordAndRecipientOutOfEveryLog()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct, recipient: "Dra. Clara Nunes");
        await using var host = await HostAsync(s);

        // Act
        using (await CheckAsync(host, link.Token))
        using (await OpenAsync(host, link.Token, "senha-errada-marcador"))
        using (await OpenAsync(host, link.Token, link.Password))
        {
        }

        // Assert
        host.Logs.Text.Should().Contain("/api/public/shares/open", "the capture must see the request logging");
        host.Logs.Text.Should().NotContain(link.Token).And.NotContain(link.Password).And.NotContain("senha-errada-marcador")
            .And.NotContain("Clara Nunes").And.NotContain("Ana Silva");
    }

    [Fact]
    public async Task Audit_NeverHoldsTheTokenPasswordOrRecipient()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct, recipient: "Dra. Clara Nunes");
        await s.OpenAsync(link.Token, link.Password, Ct);
        await using var db = s.Db.CreateDbContext();
        await ShareService.RevokeAsync(db, s.Provider, link.Id, Now, Ct);

        // Act
        var values = await db.AuditEntries.AsNoTracking().Select(a => a.Action + " " + a.PreviousValue + " " + a.NewValue).ToListAsync(Ct);

        // Assert
        values.Should().HaveCount(3);
        values.Should().NotContain(v => v.Contains(link.Token) || v.Contains(link.Password) || v.Contains("Clara") || v.Contains("Continuidade") || v.Contains("Finalidade"));
    }
}
