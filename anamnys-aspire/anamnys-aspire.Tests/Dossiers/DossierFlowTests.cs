using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text.Json;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Dossiers;
using Anamnys.Server.Integrity;
using Anamnys.Server.Notifications;
using Anamnys.Server.Retention;
using Anamnys.Server.Storage;
using Anamnys.Tests.Integrity;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;
using static Anamnys.Tests.Integrity.IntegrityTestData;

namespace Anamnys.Tests.Dossiers;

// The request flow, worker, restart handling, sweep, download and delivery receipt
// (design/specs/2026-11-04-patient-dossier-design.md §2, §3, §5, §7).
[Collection(SharedAppHostCollection.Name)]
public class DossierFlowTests(SharedAppHostFixture fixture)
{
    private const string PatientName = "Benedita";
    private const string NoteMarker = "NOTA-DO-MOTIVO-7c1e";
    private static readonly DateOnly October1 = new(2026, 10, 1);
    private static readonly DateOnly October31 = new(2026, 10, 31);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DossierFlowTests() => QuestPDF.Settings.License = LicenseType.Community;

    private sealed record Seed(RetentionScratch Db, FakeNoteFileStore Store, Guid Provider, Guid Patient, Guid Note) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Seed> SeedAsync()
    {
        var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, PatientName, Ct);
        var note = await SignedNoteAsync(s, provider, patient, new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero), Ct);
        return new Seed(s, new FakeNoteFileStore(), provider, patient, note);
    }

    private static DossierRequestBody Body(string? reason = "crp", string? note = null, DateOnly? from = null, DateOnly? to = null) =>
        new(reason, note, from, to);

    private static ServiceProvider ServicesFor(Seed s, INoteFileStore store, ITimestampAuthority tsa)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => s.Db.CreateDbContext());
        services.AddSingleton(store);
        services.AddSingleton(tsa);
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> RequestAsync(Seed s, DossierRequestBody? body = null, Guid? patient = null, DateTimeOffset? at = null)
    {
        await using var db = s.Db.CreateDbContext();
        var result = await DossierService.RequestAsync(db, s.Provider, patient ?? s.Patient, body ?? Body(), at ?? Now.AddMinutes(-1), Ct);
        result.Outcome.Should().Be(DossierOutcome.Ok);
        return result.Id!.Value;
    }

    private static async Task RunAsync(Seed s, Guid dossier, INoteFileStore? store = null, ITimestampAuthority? tsa = null)
    {
        await using var services = ServicesFor(s, store ?? s.Store, tsa ?? Tsa);
        await DossierWorker.RunAsync(
            new DossierJob(dossier, s.Provider), services.GetRequiredService<IServiceScopeFactory>(), new FakeTimeProvider(Now),
            NullLogger.Instance, Ct);
    }

    private static async Task<Dossier> RowAsync(Seed s, Guid dossier)
    {
        await using var db = s.Db.CreateDbContext();
        return await db.Dossiers.AsNoTracking().SingleAsync(x => x.Id == dossier, Ct);
    }

    private static async Task<Guid> DoneAsync(Seed s)
    {
        var id = await RequestAsync(s);
        await RunAsync(s, id);
        (await RowAsync(s, id)).Status.Should().Be("done");
        return id;
    }

    private static string PdfText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        // Spaces are not reliable in extracted PDF text: compare without them.
        return string.Concat(document.GetPages().Select(p => p.Text)).Replace(" ", "");
    }

    private static Task<long> AuditCountAsync(Seed s, string action) =>
        s.Db.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "ActorId" = @p AND "Action" = @a""", Ct, ("p", s.Provider), ("a", action));

    [Fact]
    public async Task RequestAsync_Valid_QueuesNumbersPerProviderAndAuditsReasonAndPeriodOnly()
    {
        // Arrange
        await using var s = await SeedAsync();
        var second = await s.Db.AddPatientAsync(s.Provider, "Outra", Ct);
        var otherProvider = await s.Db.AddProviderAsync(Ct);
        var stranger = await s.Db.AddPatientAsync(otherProvider, "Estranha", Ct);
        await using var db = s.Db.CreateDbContext();

        // Act
        var first = await DossierService.RequestAsync(
            db, s.Provider, s.Patient, Body("other", $"  {NoteMarker}  ", October1, October31), Now, Ct);
        var next = await DossierService.RequestAsync(db, s.Provider, second, Body("court"), Now, Ct);
        var theirs = await DossierService.RequestAsync(db, otherProvider, stranger, Body("patient_request", "ignorada"), Now, Ct);

        // Assert
        first.Outcome.Should().Be(DossierOutcome.Ok);
        first.Status.Should().Be("queued");
        var row = await RowAsync(s, first.Id!.Value);
        row.Should().Match<Dossier>(d => d.Number == 1 && d.Status == "queued" && d.Reason == "other" && d.ReasonNote == NoteMarker
            && d.PeriodFrom == October1 && d.PeriodTo == October31 && d.RequestedAt == Now && d.GeneratedAt == null
            && d.ProviderId == s.Provider && d.PatientId == s.Patient);
        (await RowAsync(s, next.Id!.Value)).Number.Should().Be(2);
        var foreign = await RowAsync(s, theirs.Id!.Value);
        foreign.Number.Should().Be(1);
        foreign.ReasonNote.Should().BeNull();
        var audits = await db.AuditEntries.AsNoTracking().Where(a => a.ActorId == s.Provider && a.NoteId == null).ToListAsync(Ct);
        audits.Should().HaveCount(2).And.OnlyContain(a => a.Action == "dossier_requested" && a.NoteId == null && a.DocumentId == null
            && a.FieldChanged == "dossier" && a.PreviousValue == null);
        audits.Select(a => a.NewValue).Should().BeEquivalentTo(
            $"{first.Id} other 2026-10-01..2026-10-31", $"{next.Id} court -..-");
        audits.Should().NotContain(a => a.NewValue!.Contains(NoteMarker) || a.NewValue.Contains(PatientName));
    }

    [Theory]
    [InlineData(null, null, null, null, DossierOutcome.InvalidReason)]
    [InlineData("CRP", null, null, null, DossierOutcome.InvalidReason)]
    [InlineData("subpoena", null, null, null, DossierOutcome.InvalidReason)]
    [InlineData("other", null, null, null, DossierOutcome.ReasonNoteRequired)]
    [InlineData("other", "   ", null, null, DossierOutcome.ReasonNoteRequired)]
    [InlineData("other", "201", null, null, DossierOutcome.ReasonNoteRequired)]
    [InlineData("crp", null, "2026-10-02", "2026-10-01", DossierOutcome.InvalidPeriod)]
    public void Validate_BadInput_ReturnsTheCode(string? reason, string? note, string? from, string? to, DossierOutcome expected)
    {
        // Arrange
        var body = Body(reason, note == "201" ? new string('a', 201) : note, Date(from), Date(to));

        // Act
        var outcome = DossierService.Validate(body);

        // Assert
        outcome.Should().Be(expected);
    }

    [Theory]
    [InlineData("other", "200", "2026-10-01", "2026-10-01")]
    [InlineData("court", null, "2026-10-01", null)]
    [InlineData("patient_request", null, null, "2026-10-31")]
    [InlineData("crp", "a note sent with another reason is dropped", null, null)]
    public void Validate_GoodInput_ReturnsNull(string reason, string? note, string? from, string? to)
    {
        // Arrange
        var body = Body(reason, note == "200" ? new string('a', 200) : note, Date(from), Date(to));

        // Act
        var outcome = DossierService.Validate(body);

        // Assert
        outcome.Should().BeNull();
    }

    [Fact]
    public void Validate_NoBody_ReturnsInvalidReason()
    {
        // Act
        var outcome = DossierService.Validate(null);

        // Assert
        outcome.Should().Be(DossierOutcome.InvalidReason);
    }

    private static DateOnly? Date(string? iso) =>
        iso is null ? null : DateOnly.ParseExact(iso, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task RequestAsync_InvalidPeriod_WritesNothing()
    {
        // Arrange
        await using var s = await SeedAsync();
        await using var db = s.Db.CreateDbContext();

        // Act
        var result = await DossierService.RequestAsync(db, s.Provider, s.Patient, Body("crp", null, October31, October1), Now, Ct);

        // Assert
        result.Outcome.Should().Be(DossierOutcome.InvalidPeriod);
        (await db.Dossiers.CountAsync(Ct)).Should().Be(0);
        (await AuditCountAsync(s, "dossier_requested")).Should().Be(0);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task RequestAsync_OneQueuedOrRunning_ReturnsInProgressAndWritesNothing(string status)
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await RequestAsync(s);
        await s.Db.ExecuteAsync("""UPDATE "Dossiers" SET "Status" = @s WHERE "Id" = @id""", Ct, ("s", status), ("id", id));
        var other = await s.Db.AddPatientAsync(s.Provider, "Outra", Ct);
        await using var db = s.Db.CreateDbContext();

        // Act
        var result = await DossierService.RequestAsync(db, s.Provider, s.Patient, Body(), Now, Ct);
        var otherPatient = await DossierService.RequestAsync(db, s.Provider, other, Body(), Now, Ct);

        // Assert
        result.Outcome.Should().Be(DossierOutcome.InProgress);
        otherPatient.Outcome.Should().Be(DossierOutcome.Ok);
        (await db.Dossiers.CountAsync(x => x.PatientId == s.Patient, Ct)).Should().Be(1);
        (await AuditCountAsync(s, "dossier_requested")).Should().Be(2);
    }

    [Fact]
    public async Task RequestAsync_ForeignOrDiscardedPatient_ReturnsNotFound_AndArchivedIsAccepted()
    {
        // Arrange
        await using var s = await SeedAsync();
        var otherProvider = await s.Db.AddProviderAsync(Ct);
        var stranger = await s.Db.AddPatientAsync(otherProvider, "Estranha", Ct);
        var discarded = await s.Db.AddPatientAsync(s.Provider, "Descartada", Ct, discardedAt: Now.AddDays(-1));
        var archived = await s.Db.AddPatientAsync(s.Provider, "Arquivada", Ct);
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @p""", Ct, ("p", archived));
        await using var db = s.Db.CreateDbContext();

        // Act
        var foreign = await DossierService.RequestAsync(db, s.Provider, stranger, Body(), Now, Ct);
        var gone = await DossierService.RequestAsync(db, s.Provider, discarded, Body(), Now, Ct);
        var unknown = await DossierService.RequestAsync(db, s.Provider, Guid.NewGuid(), Body(), Now, Ct);
        var kept = await DossierService.RequestAsync(db, s.Provider, archived, Body(), Now, Ct);

        // Assert
        foreign.Outcome.Should().Be(DossierOutcome.NotFound);
        gone.Outcome.Should().Be(DossierOutcome.NotFound);
        unknown.Outcome.Should().Be(DossierOutcome.NotFound);
        kept.Outcome.Should().Be(DossierOutcome.Ok);
        (await db.Dossiers.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_QueuedDossier_BuildsHashesStampsUploadsCompletesAndNotifies()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await RequestAsync(s, Body("other", NoteMarker));

        // Act
        await RunAsync(s, id);

        // Assert
        var row = await RowAsync(s, id);
        var key = $"dossiers/{s.Provider}/{id}.zip";
        row.Status.Should().Be("done");
        row.ObjectKey.Should().Be(key);
        var zip = s.Store.Objects[key];
        row.SizeBytes.Should().Be(zip.Length);
        row.ZipSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(zip)));
        row.GeneratedAt.Should().Be(Now);
        row.ExpiresAt.Should().Be(Now.AddDays(7));
        row.IntegrityVerified.Should().BeTrue();
        row.FailureReason.Should().BeNull();
        row.AnchorToken.Should().NotBeNull();
        // The token stamps the ZIP's own digest (openssl ts -verify -data <zip>), not a hash of its hex text.
        var digest = SHA256.HashData(zip);
        Rfc3161TimestampToken.TryDecode(row.AnchorToken, out var decoded, out _).Should().BeTrue();
        decoded!.TokenInfo.GetMessageHash().ToArray().Should().Equal(digest);
        Tsa.VerifyDigest(row.AnchorToken!, digest).Should().Be(row.AnchoredAt).And.NotBeNull();
        Tsa.Verify(row.AnchorToken!, row.ZipSha256!).Should().BeNull();

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var manifest = DossierManifest.FromJson(row.Manifest!)!;
        manifest.DossierId.Should().Be(id);
        manifest.Number.Should().Be(row.Number);
        manifest.Files.Select(f => f.Path).Should().BeEquivalentTo(archive.Entries.Select(e => e.FullName).Where(n => n != "manifesto.json"));
        foreach (var file in manifest.Files)
        {
            await using var stream = archive.GetEntry(file.Path)!.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, Ct);
            Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())).Should().Be(file.Sha256, file.Path);
        }
        key.Should().NotContain(PatientName);
        row.Manifest.Should().NotContain(PatientName).And.NotContain(NoteMarker);

        await using var db = s.Db.CreateDbContext();
        var notification = await db.Notifications.AsNoTracking().SingleAsync(n => n.ProviderId == s.Provider, Ct);
        notification.Should().Match<Notification>(n => n.Kind == "dossier_ready" && n.Channel == "in_app" && n.SubjectType == "dossier"
            && n.SubjectId == id && n.ReadAt == null);
    }

    [Fact]
    public async Task RunAsync_ReadyNotice_IsListedInTheBellOfItsProviderOnlyWithThePatient()
    {
        // Arrange
        await using var s = await SeedAsync();
        var other = await s.Db.AddProviderAsync(Ct);
        var id = await DoneAsync(s);
        await using var db = s.Db.CreateDbContext();

        // Act
        var page = await ProviderNotifications.ListAsync(db, s.Provider, 1, Ct);
        var otherPage = await ProviderNotifications.ListAsync(db, other, 1, Ct);

        // Assert
        var item = page.Items.Should().ContainSingle().Subject;
        item.Kind.Should().Be("dossier_ready");
        item.PatientId.Should().Be(s.Patient);
        item.PatientName.Should().Be($"{PatientName} Silva");
        item.ExportId.Should().BeNull();
        item.CreatedAt.Should().Be(Now);
        page.UnreadCount.Should().Be(1);
        otherPage.Items.Should().BeEmpty();
        id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunAsync_TsaDown_IsDoneWithoutTokenAndTheReceiptSaysSemCarimbo()
    {
        // Arrange
        await using var s = await SeedAsync();
        using var down = new FakeTimestampAuthority { Down = true };
        var id = await RequestAsync(s);

        // Act
        await RunAsync(s, id, tsa: down);

        // Assert
        var row = await RowAsync(s, id);
        row.Status.Should().Be("done");
        row.AnchorToken.Should().BeNull();
        row.AnchoredAt.Should().BeNull();
        down.Requests.Should().Be(1);
        await using var db = s.Db.CreateDbContext();
        var receipt = await DossierService.ReceiptAsync(db, s.Provider, id, Now, Ct);
        receipt.Outcome.Should().Be(DossierOutcome.Ok);
        PdfText(receipt.Pdf!).Should().Contain("Carimbodetempo:semcarimbo");
        (await DossierService.ListAsync(db, s.Provider, s.Patient, 1, Now, Ct))!.Items.Single().Anchored.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_TamperedNote_IsDoneWithIntegrityNotVerified()
    {
        // Arrange
        await using var s = await SeedAsync();
        await s.Db.ExecuteAsync("""UPDATE "NoteSections" SET "Content" = 'alterado' WHERE "NoteId" = @n AND "Key" = 'data'""", Ct, ("n", s.Note));
        var id = await RequestAsync(s);

        // Act
        await RunAsync(s, id);

        // Assert
        var row = await RowAsync(s, id);
        row.Status.Should().Be("done");
        row.IntegrityVerified.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_StoreDown_MarksFailedWithoutNotificationAndANewRequestIsAccepted()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await RequestAsync(s);

        // Act
        await RunAsync(s, id, store: new FakeNoteFileStore { Fail = true });

        // Assert
        var row = await RowAsync(s, id);
        row.Status.Should().Be("failed");
        row.FailureReason.Should().Be("dossier_error");
        row.ObjectKey.Should().BeNull();
        row.GeneratedAt.Should().BeNull();
        await using var db = s.Db.CreateDbContext();
        (await db.Notifications.CountAsync(n => n.ProviderId == s.Provider, Ct)).Should().Be(0);
        (await DossierService.RequestAsync(db, s.Provider, s.Patient, Body(), Now, Ct)).Outcome.Should().Be(DossierOutcome.Ok);
    }

    [Fact]
    public async Task RunAsync_FailsAfterTheUpload_DeletesTheObjectAndMarksFailed()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await RequestAsync(s);
        var store = new HookedStore(s.Store, () => throw new HttpRequestException("lost response"));

        // Act
        await RunAsync(s, id, store: store);

        // Assert
        (await RowAsync(s, id)).Status.Should().Be("failed");
        s.Store.Objects.Should().BeEmpty();
        s.Store.Deleted.Should().ContainSingle($"dossiers/{s.Provider}/{id}.zip");
    }

    [Fact]
    public async Task RunAsync_RowNoLongerRunningAtCompletion_DeletesTheUploadedObject()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await RequestAsync(s);
        var store = new HookedStore(s.Store, () => s.Db.ExecuteAsync(
            """UPDATE "Dossiers" SET "Status" = 'failed', "FailureReason" = 'interrupted' WHERE "Id" = @id""", Ct, ("id", id)));

        // Act
        await RunAsync(s, id, store: store);

        // Assert
        var row = await RowAsync(s, id);
        row.Status.Should().Be("failed");
        row.FailureReason.Should().Be("interrupted");
        s.Store.Objects.Should().BeEmpty();
        await using var db = s.Db.CreateDbContext();
        (await db.Notifications.CountAsync(n => n.ProviderId == s.Provider, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_DossierNotQueued_DoesNothing()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await RequestAsync(s);
        await s.Db.ExecuteAsync("""UPDATE "Dossiers" SET "Status" = 'failed' WHERE "Id" = @id""", Ct, ("id", id));

        // Act
        await RunAsync(s, id);

        // Assert
        s.Store.Puts.Should().Be(0);
        (await RowAsync(s, id)).Status.Should().Be("failed");
    }

    [Fact]
    public async Task RecoverAsync_AtStart_FailsRunningAndEnqueuesQueuedFromBeforeTheStart()
    {
        // Arrange
        await using var s = await SeedAsync();
        var second = await s.Db.AddPatientAsync(s.Provider, "Segunda", Ct);
        var third = await s.Db.AddPatientAsync(s.Provider, "Terceira", Ct);
        var running = await RequestAsync(s, at: Now.AddHours(-2));
        await s.Db.ExecuteAsync("""UPDATE "Dossiers" SET "Status" = 'running' WHERE "Id" = @id""", Ct, ("id", running));
        var queued = await RequestAsync(s, patient: second, at: Now.AddHours(-1));
        var fresh = await RequestAsync(s, patient: third, at: Now.AddMinutes(5));
        var queue = new DossierQueue();
        await using var db = s.Db.CreateDbContext();

        // Act
        await DossierSweep.RecoverAsync(db, queue, Now, NullLogger.Instance, Ct);

        // Assert
        var failed = await RowAsync(s, running);
        failed.Status.Should().Be("failed");
        failed.FailureReason.Should().Be("interrupted");
        (await RowAsync(s, queued)).Status.Should().Be("queued");
        (await RowAsync(s, fresh)).Status.Should().Be("queued");
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job.Should().Be(new DossierJob(queued, s.Provider));
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExpireAsync_DonePastItsLifetime_DeletesTheObjectMarksExpiredAndTheReceiptStillWorks()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await DoneAsync(s);
        var key = (await RowAsync(s, id)).ObjectKey!;
        var later = Now.AddDays(7);
        await using var db = s.Db.CreateDbContext();

        // Act
        var before = await DossierService.ExpireAsync(db, s.Store, later.AddSeconds(-1), NullLogger.Instance, Ct);
        var expired = await DossierService.ExpireAsync(db, s.Store, later, NullLogger.Instance, Ct);

        // Assert
        before.Should().Be(0);
        expired.Should().Be(1);
        s.Store.Objects.Should().NotContainKey(key);
        var row = await RowAsync(s, id);
        row.Should().Match<Dossier>(d => d.Status == "expired" && d.ObjectKey == null && d.ExpiredAt == later && d.GeneratedAt == Now
            && d.ZipSha256 != null && d.Manifest != null);
        (await DossierService.DownloadAsync(db, s.Store, s.Provider, id, later, Ct)).Outcome.Should().Be(DossierOutcome.Expired);
        var receipt = await DossierService.ReceiptAsync(db, s.Provider, id, later, Ct);
        receipt.Outcome.Should().Be(DossierOutcome.Ok);
        PdfText(receipt.Pdf!).Should().Contain("expiradoem10/11/2026").And.Contain(row.ZipSha256!);
    }

    [Fact]
    public async Task ExpireAsync_DeleteFails_KeepsTheRowDoneForTheNextSweep()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await DoneAsync(s);
        s.Store.FailDelete = true;
        await using var db = s.Db.CreateDbContext();

        // Act
        await DossierService.ExpireAsync(db, s.Store, Now.AddDays(8), NullLogger.Instance, Ct);

        // Assert
        var row = await RowAsync(s, id);
        row.Status.Should().Be("done");
        row.ObjectKey.Should().NotBeNull();
    }

    [Fact]
    public async Task DownloadAsync_Done_ReturnsAOneHourLinkWithoutThePatientNameAndAudits()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await DoneAsync(s);
        await using var db = s.Db.CreateDbContext();

        // Act
        var result = await DossierService.DownloadAsync(db, s.Store, s.Provider, id, Now.AddHours(1), Ct);

        // Assert
        result.Outcome.Should().Be(DossierOutcome.Ok);
        result.Url!.ToString().Should().Contain($"dossiers/{s.Provider}/{id}.zip").And.Contain("name=anamnys-dossie-2026-11-03-001.zip")
            .And.Contain("ttl=3600").And.NotContain(PatientName);
        result.ExpiresAt.Should().Be(Now.AddHours(2));
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.ActorId == s.Provider && a.Action == "dossier_downloaded", Ct);
        audit.NewValue.Should().Be(id.ToString());
        audit.NoteId.Should().BeNull();
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("failed")]
    public async Task DownloadAndReceipt_NotGenerated_ReturnNotReadyWithoutAudit(string status)
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await RequestAsync(s);
        await s.Db.ExecuteAsync(
            """UPDATE "Dossiers" SET "Status" = @s, "FailureReason" = CASE WHEN @s = 'failed' THEN 'dossier_error' END WHERE "Id" = @id""",
            Ct, ("s", status), ("id", id));
        await using var db = s.Db.CreateDbContext();

        // Act
        var download = await DossierService.DownloadAsync(db, s.Store, s.Provider, id, Now, Ct);
        var receipt = await DossierService.ReceiptAsync(db, s.Provider, id, Now, Ct);

        // Assert
        download.Outcome.Should().Be(DossierOutcome.NotReady);
        receipt.Outcome.Should().Be(DossierOutcome.NotReady);
        (await AuditCountAsync(s, "dossier_downloaded")).Should().Be(0);
        (await AuditCountAsync(s, "dossier_receipt_downloaded")).Should().Be(0);
    }

    [Fact]
    public async Task DownloadAndReceipt_AnotherProvidersDossier_ReturnNotFoundWithoutAudit()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await DoneAsync(s);
        var other = await s.Db.AddProviderAsync(Ct);
        await using var db = s.Db.CreateDbContext();

        // Act
        var download = await DossierService.DownloadAsync(db, s.Store, other, id, Now, Ct);
        var receipt = await DossierService.ReceiptAsync(db, other, id, Now, Ct);
        var unknown = await DossierService.DownloadAsync(db, s.Store, s.Provider, Guid.NewGuid(), Now, Ct);

        // Assert
        download.Outcome.Should().Be(DossierOutcome.NotFound);
        receipt.Outcome.Should().Be(DossierOutcome.NotFound);
        unknown.Outcome.Should().Be(DossierOutcome.NotFound);
        (await db.AuditEntries.CountAsync(a => a.Action == "dossier_downloaded" || a.Action == "dossier_receipt_downloaded", Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ReceiptAsync_Done_ShowsTheZipHashTimestampManifestAndStatusAndAudits()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await RequestAsync(s, Body("other", NoteMarker, October1, October31));
        await RunAsync(s, id);
        var row = await RowAsync(s, id);
        await using var db = s.Db.CreateDbContext();

        // Act
        var receipt = await DossierService.ReceiptAsync(db, s.Provider, id, Now.AddDays(1), Ct);

        // Assert
        receipt.Outcome.Should().Be(DossierOutcome.Ok);
        receipt.FileName.Should().Be("anamnys-dossie-001-comprovante.pdf");
        var text = PdfText(receipt.Pdf!);
        text.Should().Contain("COMPROVANTEDEENTREGADODOSSIÊ").And.Contain("nº001");
        text.Should().Contain($"{PatientName}Silva").And.Contain("Dra.Teste").And.Contain("CRP123456/06");
        text.Should().Contain(row.ZipSha256!);
        text.Should().Contain("emitidoporAnamnysTestTSA");
        text.Should().Contain("prontoparadownloadaté10/11/2026");
        text.Should().Contain("de01/10/2026a31/10/2026").And.Contain($"Outro:{NoteMarker}");
        text.Should().Contain("anamnys-dossie-001.tsr")
            .And.Contain("opensslts-verify-dataanamnys-dossie-2026-11-03-001.zip-inanamnys-dossie-001.tsr-token_in-CAfile");
        foreach (var file in DossierManifest.FromJson(row.Manifest!)!.Files)
        {
            text.Should().Contain(file.Path).And.Contain(file.Sha256);
        }
        text.Should().NotContainEquivalentOf("cofre");
        var audit = await db.AuditEntries.AsNoTracking()
            .SingleAsync(a => a.ActorId == s.Provider && a.Action == "dossier_receipt_downloaded", Ct);
        audit.NewValue.Should().Be(id.ToString());
        (await db.AuditEntries.AsNoTracking().Where(a => a.ActorId == s.Provider).Select(a => a.NewValue).ToListAsync(Ct))
            .Should().NotContain(v => v != null && v.Contains(NoteMarker));
    }

    [Fact]
    public async Task ListAsync_SevenDossiers_PagesFiveNewestFirstWithoutTheNote()
    {
        // Arrange
        await using var s = await SeedAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            var id = await RequestAsync(s, Body("other", NoteMarker), at: Now.AddHours(-10 + i));
            await s.Db.ExecuteAsync("""UPDATE "Dossiers" SET "Status" = 'failed', "FailureReason" = 'dossier_error' WHERE "Id" = @id""", Ct, ("id", id));
            ids.Add(id);
        }
        // The newest is done but past its lifetime (the sweep has not run yet).
        await s.Db.ExecuteAsync(
            """
            UPDATE "Dossiers" SET "Status" = 'done', "FailureReason" = NULL, "GeneratedAt" = @g, "ExpiresAt" = @e, "ObjectKey" = 'k',
                                  "ZipSha256" = repeat('a', 64), "SizeBytes" = 10, "IntegrityVerified" = true
            WHERE "Id" = @id
            """, Ct, ("id", ids[^1]), ("g", Now.AddDays(-8)), ("e", Now.AddDays(-1)));
        var other = await s.Db.AddProviderAsync(Ct);
        await using var db = s.Db.CreateDbContext();

        // Act
        var first = await DossierService.ListAsync(db, s.Provider, s.Patient, 1, Now, Ct);
        var second = await DossierService.ListAsync(db, s.Provider, s.Patient, 2, Now, Ct);
        var clamped = await DossierService.ListAsync(db, s.Provider, s.Patient, 0, Now, Ct);
        var foreign = await DossierService.ListAsync(db, other, s.Patient, 1, Now, Ct);

        // Assert
        first!.Total.Should().Be(7);
        first.PageSize.Should().Be(5);
        first.Items.Select(i => i.Id).Should().Equal(ids.AsEnumerable().Reverse().Take(5));
        first.Items[0].Should().Match<DossierListItem>(i => i.Status == "expired" && i.IntegrityVerified == true && i.SizeBytes == 10 && !i.Anchored);
        first.Items[1].Should().Match<DossierListItem>(i => i.Status == "failed" && i.IntegrityVerified == null && i.Reason == "other");
        second!.Items.Select(i => i.Id).Should().Equal(ids[1], ids[0]);
        second.Page.Should().Be(2);
        clamped!.Page.Should().Be(1);
        foreign.Should().BeNull();
        JsonSerializer.Serialize(first).Should().NotContain(NoteMarker);
    }

    [Fact]
    public async Task Discard_DoneDossier_DeletesTheRowAndItsObject()
    {
        // Arrange: a record whose retention ended long ago (an attended appointment in 2015) with a generated dossier.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var provider = await s.AddProviderAsync(Ct, withCrp: true);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        await s.AddAppointmentAsync(provider, patient, new DateTimeOffset(2015, 3, 2, 14, 0, 0, TimeSpan.Zero), "attended", Ct);
        var seed = new Seed(s, store, provider, patient, Guid.Empty);
        var id = await RequestAsync(seed);
        await RunAsync(seed, id);
        var key = (await RowAsync(seed, id)).ObjectKey!;
        store.Objects.Should().ContainKey(key);
        await using var db = s.CreateDbContext();
        var unreadBefore = (await ProviderNotifications.ListAsync(db, provider, 1, Ct)).UnreadCount;
        unreadBefore.Should().BeGreaterThanOrEqualTo(1);

        // Act
        var result = await DiscardService.DiscardAsync(db, store, NullLogger.Instance, provider, patient, "Ana Silva", Now, Ct);

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.Ok);
        (await s.CountAsync("""SELECT count(*) FROM "Dossiers" WHERE "PatientId" = @p""", Ct, ("p", patient))).Should().Be(0);
        store.Objects.Should().NotContainKey(key);
        var after = await ProviderNotifications.ListAsync(db, provider, 1, Ct);
        after.Items.Should().NotContain(i => i.Kind == "dossier_ready");
        after.UnreadCount.Should().Be(unreadBefore - 1);
        (await s.CountAsync("""SELECT count(*) FROM "Notifications" WHERE "SubjectType" = 'dossier' AND "SubjectId" = @id""", Ct, ("id", id)))
            .Should().Be(0);
    }

    [Fact]
    public async Task TokenAsync_Done_ReturnsTheStoredTokenAndAudits()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await DoneAsync(s);
        var row = await RowAsync(s, id);
        await using var db = s.Db.CreateDbContext();

        // Act
        var result = await DossierService.TokenAsync(db, s.Provider, id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DossierOutcome.Ok);
        result.Token.Should().Equal(row.AnchorToken);
        result.FileName.Should().Be("anamnys-dossie-001.tsr");
        Tsa.VerifyDigest(result.Token!, SHA256.HashData(s.Store.Objects[row.ObjectKey!])).Should().NotBeNull();
        var audit = await db.AuditEntries.AsNoTracking()
            .SingleAsync(a => a.ActorId == s.Provider && a.Action == "dossier_receipt_downloaded", Ct);
        audit.NewValue.Should().Be($"{id} tsr");
    }

    [Fact]
    public async Task TokenAsync_NoTokenQueuedOrForeign_AnswersWithoutAudit()
    {
        // Arrange
        await using var s = await SeedAsync();
        using var down = new FakeTimestampAuthority { Down = true };
        var unstamped = await RequestAsync(s);
        await RunAsync(s, unstamped, tsa: down);
        var second = await s.Db.AddPatientAsync(s.Provider, "Segunda", Ct);
        var queued = await RequestAsync(s, patient: second);
        var other = await s.Db.AddProviderAsync(Ct);
        await using var db = s.Db.CreateDbContext();

        // Act
        var none = await DossierService.TokenAsync(db, s.Provider, unstamped, Now, Ct);
        var notReady = await DossierService.TokenAsync(db, s.Provider, queued, Now, Ct);
        var foreign = await DossierService.TokenAsync(db, other, unstamped, Now, Ct);
        var unknown = await DossierService.TokenAsync(db, s.Provider, Guid.NewGuid(), Now, Ct);

        // Assert
        none.Outcome.Should().Be(DossierOutcome.NotFound);
        notReady.Outcome.Should().Be(DossierOutcome.NotReady);
        foreign.Outcome.Should().Be(DossierOutcome.NotFound);
        unknown.Outcome.Should().Be(DossierOutcome.NotFound);
        (await AuditCountAsync(s, "dossier_receipt_downloaded")).Should().Be(0);
    }

    [Fact]
    public async Task DownloadAsync_CloseToExpiry_CapsTheLinkAtTheDossiersExpiry()
    {
        // Arrange
        await using var s = await SeedAsync();
        var id = await DoneAsync(s);
        var at = Now.AddDays(7).AddMinutes(-10);
        await using var db = s.Db.CreateDbContext();

        // Act
        var result = await DossierService.DownloadAsync(db, s.Store, s.Provider, id, at, Ct);

        // Assert
        result.Outcome.Should().Be(DossierOutcome.Ok);
        result.ExpiresAt.Should().Be(Now.AddDays(7));
        result.Url!.ToString().Should().Contain("ttl=600");
    }

    // Delegates to the fake store; after each stored object runs a hook (which may throw, as a lost upload response would).
    private sealed class HookedStore(FakeNoteFileStore inner, Func<Task> afterPut) : INoteFileStore
    {
        public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
        {
            await inner.PutAsync(key, content, contentType, ct);
            await afterPut();
        }

        public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
        {
            await inner.PutAsync(key, content, contentType, ct);
            await afterPut();
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken ct) => inner.GetAsync(key, ct);

        public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);

        public Uri PresignGet(string key, string downloadFileName, TimeSpan validFor, string contentType = "application/pdf") =>
            inner.PresignGet(key, downloadFileName, validFor, contentType);

        public Uri PresignGetInline(string key, string contentType, TimeSpan validFor) => inner.PresignGetInline(key, contentType, validFor);
    }
}
