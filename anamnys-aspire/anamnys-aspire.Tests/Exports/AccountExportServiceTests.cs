using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Exports;
using Anamnys.Server.Notifications;
using Anamnys.Server.Storage;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.Exports;

[Collection(SharedAppHostCollection.Name)]
public class AccountExportServiceTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<AccountExport> AddExportAsync(
        AnamnysDbContext db, Guid providerId, string status, DateTimeOffset? completedAt = null, string? objectKey = null, long? size = null)
    {
        var export = new AccountExport
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            RequestedAt = (completedAt ?? Now).AddMinutes(-5),
            Status = status,
            CompletedAt = completedAt,
            ObjectKey = objectKey,
            SizeBytes = size,
        };
        db.AccountExports.Add(export);
        await db.SaveChangesAsync(Ct);
        return export;
    }

    private static ServiceProvider ServicesFor(PatientSearchSeed seed, FakeNoteFileStore store, IAccountExportBuilder builder)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => seed.CreateDbContext());
        services.AddSingleton<INoteFileStore>(store);
        services.AddSingleton(builder);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task RequestAsync_NoExportInProgress_QueuesItAndAuditsWithoutNoteOrDocument()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await AccountExportService.RequestAsync(db, seed.ProviderId, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportRequestOutcome.Ok);
        result.Status.Should().Be("queued");
        var export = await db.AccountExports.AsNoTracking().SingleAsync(x => x.Id == result.Id, Ct);
        export.ProviderId.Should().Be(seed.ProviderId);
        export.RequestedAt.Should().Be(Now);
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.ActorId == seed.ProviderId, Ct);
        audit.Action.Should().Be("account_export_requested");
        audit.NoteId.Should().BeNull();
        audit.DocumentId.Should().BeNull();
        audit.PreviousValue.Should().BeNull();
        audit.NewValue.Should().BeNull();
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task RequestAsync_OneQueuedOrRunning_ReturnsInProgressAndWritesNothing(string status)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        await AddExportAsync(db, seed.ProviderId, status);

        // Act
        var result = await AccountExportService.RequestAsync(db, seed.ProviderId, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportRequestOutcome.InProgress);
        (await db.AccountExports.CountAsync(x => x.ProviderId == seed.ProviderId, Ct)).Should().Be(1);
        (await db.AuditEntries.CountAsync(a => a.ActorId == seed.ProviderId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task RequestAsync_OtherProviderHasOneInProgress_IsAccepted()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        await using var db = seed.CreateDbContext();
        await AddExportAsync(db, other, "running");

        // Act
        var result = await AccountExportService.RequestAsync(db, seed.ProviderId, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportRequestOutcome.Ok);
    }

    [Fact]
    public async Task RequestAsync_PreviousOneFailed_IsAccepted()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        await AddExportAsync(db, seed.ProviderId, "failed");

        // Act
        var result = await AccountExportService.RequestAsync(db, seed.ProviderId, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportRequestOutcome.Ok);
    }

    [Fact]
    public async Task LatestAsync_NoExports_ReturnsNull()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var latest = await AccountExportService.LatestAsync(db, seed.ProviderId, Ct);

        // Assert
        latest.Should().BeNull();
    }

    [Fact]
    public async Task LatestAsync_SeveralExports_ReturnsTheNewestOfThisProviderWithItsExpiry()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        await using var db = seed.CreateDbContext();
        await AddExportAsync(db, seed.ProviderId, "failed");
        var done = await AddExportAsync(db, seed.ProviderId, "done", Now.AddHours(1), "exports/k.zip", 1234);
        await AddExportAsync(db, other, "running");

        // Act
        var latest = await AccountExportService.LatestAsync(db, seed.ProviderId, Ct);

        // Assert
        latest.Should().NotBeNull();
        latest!.Id.Should().Be(done.Id);
        latest.Status.Should().Be("done");
        latest.SizeBytes.Should().Be(1234);
        latest.CompletedAt.Should().Be(Now.AddHours(1));
        latest.ExpiresAt.Should().Be(Now.AddHours(1).AddDays(7));
    }

    [Fact]
    public async Task DownloadAsync_DoneExport_ReturnsOneHourLinkAndAudits()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var export = await AddExportAsync(db, seed.ProviderId, "done", Now.AddDays(-1), "exports/k.zip", 10);
        var store = new FakeNoteFileStore();

        // Act
        var result = await AccountExportService.DownloadAsync(db, store, seed.ProviderId, export.Id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportDownloadOutcome.Ok);
        result.Url!.ToString().Should().Contain("exports/k.zip").And.Contain("name=anamnys-exportacao-2026-10-22.zip").And.Contain("ttl=3600");
        result.ExpiresAt.Should().Be(Now.AddHours(1));
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.ActorId == seed.ProviderId, Ct);
        audit.Action.Should().Be("account_export_downloaded");
        audit.NoteId.Should().BeNull();
        audit.DocumentId.Should().BeNull();
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("failed")]
    public async Task DownloadAsync_NotDone_ReturnsNotReadyWithoutAudit(string status)
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var export = await AddExportAsync(db, seed.ProviderId, status);

        // Act
        var result = await AccountExportService.DownloadAsync(db, new FakeNoteFileStore(), seed.ProviderId, export.Id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportDownloadOutcome.NotReady);
        result.Url.Should().BeNull();
        (await db.AuditEntries.CountAsync(a => a.ActorId == seed.ProviderId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task DownloadAsync_ExpiredStatus_ReturnsExpired()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var export = await AddExportAsync(db, seed.ProviderId, "expired", Now.AddDays(-8));

        // Act
        var result = await AccountExportService.DownloadAsync(db, new FakeNoteFileStore(), seed.ProviderId, export.Id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportDownloadOutcome.Expired);
    }

    [Fact]
    public async Task DownloadAsync_DonePastRetentionBeforeTheSweepRan_ReturnsExpired()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var export = await AddExportAsync(db, seed.ProviderId, "done", Now.AddDays(-7), "exports/k.zip", 10);

        // Act
        var result = await AccountExportService.DownloadAsync(db, new FakeNoteFileStore(), seed.ProviderId, export.Id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(ExportDownloadOutcome.Expired);
    }

    [Fact]
    public async Task DownloadAsync_OtherProvidersExportOrUnknownId_ReturnsNotFound()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        await using var db = seed.CreateDbContext();
        var export = await AddExportAsync(db, other, "done", Now.AddDays(-1), "exports/k.zip", 10);

        // Act
        var foreign = await AccountExportService.DownloadAsync(db, new FakeNoteFileStore(), seed.ProviderId, export.Id, Now, Ct);
        var unknown = await AccountExportService.DownloadAsync(db, new FakeNoteFileStore(), seed.ProviderId, Guid.NewGuid(), Now, Ct);

        // Assert
        foreign.Outcome.Should().Be(ExportDownloadOutcome.NotFound);
        unknown.Outcome.Should().Be(ExportDownloadOutcome.NotFound);
        (await db.AuditEntries.CountAsync(a => a.ActorId == seed.ProviderId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_QueuedExport_BuildsUploadsMarksDoneAndNotifies()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var export = await AddExportAsync(db, seed.ProviderId, "queued");
        var store = new FakeNoteFileStore();
        await using var services = ServicesFor(seed, store, new MinimalBuilder());
        var clock = new FakeTimeProvider(Now);

        // Act
        await ExportWorker.RunAsync(new ExportJob(export.Id, seed.ProviderId), services.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger.Instance, Ct);

        // Assert
        var key = $"exports/{seed.ProviderId}/{export.Id}.zip";
        store.Objects.Should().ContainKey(key);
        store.Objects[key][..2].Should().Equal((byte)'P', (byte)'K');
        var row = await db.AccountExports.AsNoTracking().SingleAsync(x => x.Id == export.Id, Ct);
        row.Status.Should().Be("done");
        row.ObjectKey.Should().Be(key);
        row.SizeBytes.Should().Be(store.Objects[key].Length);
        row.CompletedAt.Should().Be(Now);
        row.FailureReason.Should().BeNull();
        var notification = await db.Notifications.AsNoTracking().SingleAsync(n => n.ProviderId == seed.ProviderId, Ct);
        notification.Kind.Should().Be("account_export_ready");
        notification.Channel.Should().Be("in_app");
        notification.SubjectType.Should().Be("account_export");
        notification.SubjectId.Should().Be(export.Id);
        notification.ReadAt.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_ExportNotificationIsListedInTheBellOfItsProviderOnly()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        await using var db = seed.CreateDbContext();
        var export = await AddExportAsync(db, seed.ProviderId, "queued");
        await using var services = ServicesFor(seed, new FakeNoteFileStore(), new MinimalBuilder());
        await ExportWorker.RunAsync(new ExportJob(export.Id, seed.ProviderId), services.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Act
        var page = await ProviderNotifications.ListAsync(db, seed.ProviderId, 1, Ct);
        var otherPage = await ProviderNotifications.ListAsync(db, other, 1, Ct);

        // Assert
        page.UnreadCount.Should().Be(1);
        page.Items.Should().ContainSingle();
        page.Items[0].Kind.Should().Be("account_export_ready");
        page.Items[0].ExportId.Should().Be(export.Id);
        page.Items[0].AppointmentId.Should().BeNull();
        page.Items[0].CreatedAt.Should().Be(Now);
        otherPage.Items.Should().BeEmpty();
        otherPage.UnreadCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_BuilderThrows_MarksFailedWithoutUploadOrNotification()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var export = await AddExportAsync(db, seed.ProviderId, "queued");
        var store = new FakeNoteFileStore();
        await using var services = ServicesFor(seed, store, new ThrowingBuilder());

        // Act
        await ExportWorker.RunAsync(new ExportJob(export.Id, seed.ProviderId), services.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        var row = await db.AccountExports.AsNoTracking().SingleAsync(x => x.Id == export.Id, Ct);
        row.Status.Should().Be("failed");
        row.FailureReason.Should().Be("export_error");
        row.ObjectKey.Should().BeNull();
        row.CompletedAt.Should().BeNull();
        store.Objects.Should().BeEmpty();
        (await db.Notifications.CountAsync(n => n.ProviderId == seed.ProviderId, Ct)).Should().Be(0);
        (await AccountExportService.RequestAsync(db, seed.ProviderId, Now, Ct)).Outcome.Should().Be(ExportRequestOutcome.Ok);
    }

    [Fact]
    public async Task RunAsync_StoreIsDown_MarksFailed()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var export = await AddExportAsync(db, seed.ProviderId, "queued");
        var store = new FakeNoteFileStore { Fail = true };
        await using var services = ServicesFor(seed, store, new MinimalBuilder());

        // Act
        await ExportWorker.RunAsync(new ExportJob(export.Id, seed.ProviderId), services.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        var row = await db.AccountExports.AsNoTracking().SingleAsync(x => x.Id == export.Id, Ct);
        row.Status.Should().Be("failed");
        row.ObjectKey.Should().BeNull();
        (await db.Notifications.CountAsync(n => n.ProviderId == seed.ProviderId, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ExportNotQueued_DoesNothing()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var export = await AddExportAsync(db, seed.ProviderId, "failed");
        var store = new FakeNoteFileStore();
        await using var services = ServicesFor(seed, store, new MinimalBuilder());

        // Act
        await ExportWorker.RunAsync(new ExportJob(export.Id, seed.ProviderId), services.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(Now), NullLogger.Instance, Ct);

        // Assert
        store.Puts.Should().Be(0);
        (await db.AccountExports.AsNoTracking().SingleAsync(x => x.Id == export.Id, Ct)).Status.Should().Be("failed");
    }

    [Fact]
    public async Task FailInterruptedAsync_QueuedAndRunning_AreFailedAndDoneIsKept()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        var other = await seed.AddProviderAsync(Ct);
        await using var db = seed.CreateDbContext();
        var running = await AddExportAsync(db, seed.ProviderId, "running");
        var queued = await AddExportAsync(db, other, "queued");
        var done = await AddExportAsync(db, seed.ProviderId, "done", Now.AddDays(-1), "exports/k.zip", 10);

        // Act
        var failed = await AccountExportService.FailInterruptedAsync(db, Now, Ct);

        // Assert
        failed.Should().BeGreaterThanOrEqualTo(2);
        var rows = await db.AccountExports.AsNoTracking().Where(x => x.Id == running.Id || x.Id == queued.Id || x.Id == done.Id).ToDictionaryAsync(x => x.Id, Ct);
        rows[running.Id].Status.Should().Be("failed");
        rows[running.Id].FailureReason.Should().Be("interrupted");
        rows[queued.Id].Status.Should().Be("failed");
        rows[done.Id].Status.Should().Be("done");
    }

    [Fact]
    public async Task FailInterruptedAsync_RequestedAfterTheProcessStarted_IsKept()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var fresh = await AddExportAsync(db, seed.ProviderId, "queued");

        // Act
        await AccountExportService.FailInterruptedAsync(db, Now.AddMinutes(-10), Ct);

        // Assert
        (await db.AccountExports.AsNoTracking().SingleAsync(x => x.Id == fresh.Id, Ct)).Status.Should().Be("queued");
    }

    [Fact]
    public void DeleteLeftoverTempFiles_DeletesOldExportZipsOnly()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("anamnys-test-").FullName;
        try
        {
            var old = Path.Combine(dir, "anamnys-export-old.zip");
            var fresh = Path.Combine(dir, "anamnys-export-fresh.zip");
            var other = Path.Combine(dir, "other.zip");
            foreach (var p in new[] { old, fresh, other }) File.WriteAllText(p, "x");
            var startedAt = DateTimeOffset.UtcNow;
            File.SetLastWriteTimeUtc(old, startedAt.UtcDateTime.AddHours(-1));
            File.SetLastWriteTimeUtc(other, startedAt.UtcDateTime.AddHours(-1));
            File.SetLastWriteTimeUtc(fresh, startedAt.UtcDateTime.AddHours(1));

            // Act
            var deleted = ExportWorker.DeleteLeftoverTempFiles(dir, startedAt, NullLogger.Instance);

            // Assert
            deleted.Should().Be(1);
            File.Exists(old).Should().BeFalse();
            File.Exists(fresh).Should().BeTrue();
            File.Exists(other).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExpireAsync_DoneOlderThanSevenDays_DeletesTheObjectAndMarksExpired()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var old = await AddExportAsync(db, seed.ProviderId, "done", Now.AddDays(-7), $"exports/{seed.ProviderId}/old.zip", 10);
        var other = await seed.AddProviderAsync(Ct);
        var recent = await AddExportAsync(db, other, "done", Now.AddDays(-6), $"exports/{other}/recent.zip", 10);
        var store = new FakeNoteFileStore();
        store.Objects[old.ObjectKey!] = [1];
        store.Objects[recent.ObjectKey!] = [1];

        // Act
        var expired = await AccountExportService.ExpireAsync(db, store, Now, NullLogger.Instance, Ct);

        // Assert
        expired.Should().BeGreaterThanOrEqualTo(1);
        store.Objects.Should().NotContainKey(old.ObjectKey!).And.ContainKey(recent.ObjectKey!);
        var oldRow = await db.AccountExports.AsNoTracking().SingleAsync(x => x.Id == old.Id, Ct);
        oldRow.Status.Should().Be("expired");
        oldRow.ObjectKey.Should().BeNull();
        oldRow.ExpiredAt.Should().Be(Now);
        oldRow.CompletedAt.Should().Be(Now.AddDays(-7));
        (await db.AccountExports.AsNoTracking().SingleAsync(x => x.Id == recent.Id, Ct)).Status.Should().Be("done");
    }

    [Fact]
    public async Task ExpireAsync_DeleteFails_KeepsTheRowDoneForTheNextSweep()
    {
        // Arrange
        await using var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await using var db = seed.CreateDbContext();
        var old = await AddExportAsync(db, seed.ProviderId, "done", Now.AddDays(-8), $"exports/{seed.ProviderId}/old.zip", 10);
        var store = new FakeNoteFileStore { FailDelete = true };

        // Act
        await AccountExportService.ExpireAsync(db, store, Now, NullLogger.Instance, Ct);

        // Assert
        var row = await db.AccountExports.AsNoTracking().SingleAsync(x => x.Id == old.Id, Ct);
        row.Status.Should().Be("done");
        row.ObjectKey.Should().NotBeNull();
    }

    private sealed class MinimalBuilder : IAccountExportBuilder
    {
        public async Task BuildAsync(Guid providerId, Stream destination, CancellationToken ct)
        {
            using var archive = new System.IO.Compression.ZipArchive(destination, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true);
            await using var stream = archive.CreateEntry("LEIA-ME.txt").Open();
            await stream.WriteAsync("export"u8.ToArray(), ct);
        }
    }

    private sealed class ThrowingBuilder : IAccountExportBuilder
    {
        public Task BuildAsync(Guid providerId, Stream destination, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
