using Anamnys.Server.Documents;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Retention;
using Anamnys.Server.Sharing;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;

namespace Anamnys.Tests.Sharing;

// The provider's side (design/specs/2026-11-01-protected-sharing-design.md §3, §6).
[Collection(SharedAppHostCollection.Name)]
public class ShareServiceTests(SharedAppHostFixture fixture)
{
    private static readonly byte[] Pdf = "%PDF-1.7 termo"u8.ToArray();
    private static DateTimeOffset Now => ShareScratch.Now;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ShareServiceTests() => QuestPDF.Settings.License = LicenseType.Community;

    private sealed class DownScanner : IVirusScanner
    {
        public Task<ScanVerdict> ScanAsync(byte[] content, CancellationToken ct) => throw new ScannerUnavailableException("down");
    }

    private Task<ShareScratch> StartAsync() => ShareScratch.CreateAsync(fixture, Ct);

    [Fact]
    public async Task RecordAuthorizationAsync_SignedDocument_StoresItAndAuditsIdsOnly()
    {
        // Arrange
        await using var s = await StartAsync();

        // Act
        var result = await s.AuthorizeAsync(Ct);

        // Assert
        result.Outcome.Should().Be(ShareOutcome.Ok);
        await using var db = s.Db.CreateDbContext();
        var row = await db.DisclosureAuthorizations.AsNoTracking().SingleAsync(Ct);
        row.ProviderId.Should().Be(s.Provider);
        row.PatientId.Should().Be(s.Patient);
        row.ConfirmedAt.Should().Be(Now);
        row.ObtainedVia.Should().Be("signed_term");
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == ShareService.AuthorizedAction, Ct);
        audit.DocumentId.Should().Be(s.Document);
        audit.NewValue.Should().Be(row.Id.ToString());
    }

    [Fact]
    public async Task RecordAuthorizationAsync_SameRecipientAndPurpose_IsRefusedWhateverTheCase()
    {
        // Arrange
        await using var s = await StartAsync();
        var first = await s.AuthorizeAsync(Ct);

        // Act
        var second = await s.AuthorizeAsync(Ct, ShareScratch.Request("DR. PEDRO LIMA", "continuidade do cuidado"));

        // Assert
        second.Outcome.Should().Be(ShareOutcome.AuthorizationExists);
        second.Id.Should().Be(first.Id!.Value);
    }

    [Fact]
    public async Task RecordAuthorizationAsync_WithTerm_StoresItUnderAnIdOnlyKey()
    {
        // Arrange
        await using var s = await StartAsync();

        // Act
        var result = await s.AuthorizeAsync(Ct, term: new TermUpload("Termo da Ana Silva.pdf", Pdf));

        // Assert
        result.Outcome.Should().Be(ShareOutcome.Ok);
        s.Store.Objects.Keys.Should().ContainSingle().Which.Should().Be($"disclosure-terms/{s.Provider}/{result.Id}");
    }

    [Theory]
    [InlineData("termo.pdf", "", ShareOutcome.TermEmpty)]
    [InlineData("termo.txt", "%PDF-1.7", ShareOutcome.TermTypeMismatch)]
    [InlineData("termo.exe", "MZ-programa", ShareOutcome.TermUnsupportedType)]
    [InlineData("termo.pdf", "%PDF-1.7 EICAR-STANDARD-ANTIVIRUS-TEST-FILE", ShareOutcome.TermInfected)]
    public async Task RecordAuthorizationAsync_BadTerm_IsRefusedAndNothingIsKept(string name, string content, ShareOutcome expected)
    {
        // Arrange
        await using var s = await StartAsync();

        // Act
        var result = await s.AuthorizeAsync(Ct, term: new TermUpload(name, System.Text.Encoding.ASCII.GetBytes(content)));

        // Assert
        result.Outcome.Should().Be(expected);
        s.Store.Objects.Should().BeEmpty();
        await using var db = s.Db.CreateDbContext();
        (await db.DisclosureAuthorizations.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task RecordAuthorizationAsync_ScannerDown_RefusesTheFile()
    {
        // Arrange
        await using var s = await StartAsync();
        s.Scanner = new DownScanner();

        // Act
        var result = await s.AuthorizeAsync(Ct, term: new TermUpload("termo.pdf", Pdf));

        // Assert
        result.Outcome.Should().Be(ShareOutcome.ScannerUnavailable);
        s.Store.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordAuthorizationAsync_StorageDown_RecordsNothing()
    {
        // Arrange
        await using var s = await StartAsync();
        s.Store.Fail = true;

        // Act
        var result = await s.AuthorizeAsync(Ct, term: new TermUpload("termo.pdf", Pdf));

        // Assert
        result.Outcome.Should().Be(ShareOutcome.StorageUnavailable);
        await using var db = s.Db.CreateDbContext();
        (await db.DisclosureAuthorizations.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task RecordAuthorizationAsync_UnsignedDocument_IsNotSigned()
    {
        // Arrange
        await using var s = await StartAsync();
        await using var db = s.Db.CreateDbContext();
        var draft = await ClinicalDocumentService.CreateAsync(db, s.Provider, s.Patient, "declaracao", null, Now, Ct);

        // Act
        var result = await s.AuthorizeAsync(Ct, document: draft.Id);

        // Assert
        result.Outcome.Should().Be(ShareOutcome.NotSigned);
    }

    [Fact]
    public async Task ShareActions_ArchivedPatient_AreRefused_AndAnExistingLinkStillOpens()
    {
        // Arrange
        await using var s = await StartAsync();
        var authorization = await s.AuthorizeAsync(Ct);
        var link = (await s.LinkAsync(authorization.Id!.Value, Ct)).Link!;
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @pa""", Ct, ("pa", s.Patient));

        // Act
        var newAuthorization = await s.AuthorizeAsync(Ct, ShareScratch.Request("Outra Pessoa"));
        var newLink = await s.LinkAsync(authorization.Id!.Value, Ct);
        var opened = await s.OpenAsync(link.Token, link.Password, Ct);

        // Assert
        newAuthorization.Outcome.Should().Be(ShareOutcome.Archived);
        newLink.Outcome.Should().Be(ShareOutcome.Archived);
        opened.Outcome.Should().Be(PublicOpenOutcome.Opened);
    }

    [Fact]
    public async Task ShareActions_SupersededDocument_RefuseNewShares_ExistingLinkKeepsTheVersion_AndTheListSaysSo()
    {
        // Arrange
        await using var s = await StartAsync();
        var authorization = await s.AuthorizeAsync(Ct);
        var link = (await s.LinkAsync(authorization.Id!.Value, Ct)).Link!;
        await using var db = s.Db.CreateDbContext();
        var newer = (await ClinicalDocumentService.CorrectAsync(db, s.Provider, s.Document, Now, Ct)).Id;

        // Act
        var newLink = await s.LinkAsync(authorization.Id!.Value, Ct);
        var opened = await s.OpenAsync(link.Token, link.Password, Ct);
        var list = await ShareService.ListAsync(db, s.Provider, s.Document, Now, Ct);

        // Assert
        newLink.Outcome.Should().Be(ShareOutcome.Superseded);
        opened.Outcome.Should().Be(PublicOpenOutcome.Opened);
        opened.FileName.Should().Be(DocumentPdfModel.FileNameFor(s.Document));
        list!.SupersededBy.Should().Be(newer!.Value);
        list.CanShare.Should().BeFalse();
        list.BlockedReason.Should().Be("document_superseded");
    }

    [Fact]
    public async Task CreateLinkAsync_WithoutAnAuthorizationOfThisDocument_IsRefused()
    {
        // Arrange
        await using var s = await StartAsync();
        var other = await s.Db.AddSignedDocumentAsync(s.Provider, s.Patient, Now.AddDays(-2), Ct);
        var foreign = await s.AuthorizeAsync(Ct, document: other);
        await using var db = s.Db.CreateDbContext();

        // Act
        var none = await ShareService.CreateLinkAsync(db, s.Limiter, s.Provider, s.Document, new CreateShareLinkRequest(null, null, null, null), Now, Ct);
        var wrong = await s.LinkAsync(foreign.Id!.Value, Ct);

        // Assert
        none.Outcome.Should().Be(ShareOutcome.AuthorizationRequired);
        wrong.Outcome.Should().Be(ShareOutcome.AuthorizationRequired);
        (await db.ShareLinks.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task CreateLinkAsync_Defaults_SevenDaysNoDownloadNoCap_StoresHashesOnly_AuditsIdsOnly()
    {
        // Arrange
        await using var s = await StartAsync();
        var authorization = await s.AuthorizeAsync(Ct);

        // Act
        var link = (await s.LinkAsync(authorization.Id!.Value, Ct)).Link!;

        // Assert
        link.ExpiresAt.Should().Be(Now.AddDays(7));
        link.DownloadEnabled.Should().BeFalse();
        link.ViewCap.Should().BeNull();
        await using var db = s.Db.CreateDbContext();
        var row = await db.ShareLinks.AsNoTracking().SingleAsync(Ct);
        row.TokenHash.Should().Be(ShareSecrets.HashToken(link.Token)).And.NotContain(link.Token);
        row.PasswordHash.Should().StartWith("$argon2id$").And.NotContain(link.Password);
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == ShareService.LinkCreatedAction, Ct);
        audit.NewValue.Should().Be($"{link.Id} {authorization.Id}");
    }

    [Fact]
    public async Task CreateLinkAsync_ThirtyDays_FitsTheDatabaseBound()
    {
        // Arrange
        await using var s = await StartAsync();
        var authorization = await s.AuthorizeAsync(Ct);

        // Act
        var result = await s.LinkAsync(authorization.Id!.Value, Ct, days: 30);

        // Assert
        result.Link!.ExpiresAt.Should().Be(Now.AddDays(30));
    }

    [Fact]
    public async Task EveryProviderAction_OnAnotherProvidersDocument_IsNotFound()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);
        var stranger = await s.Db.AddProviderAsync(Ct, withCrp: true);
        await using var db = s.Db.CreateDbContext();

        // Act
        var authorize = await s.AuthorizeAsync(Ct, ShareScratch.Request("Outra Pessoa"), provider: stranger);
        var create = await ShareService.CreateLinkAsync(db, s.Limiter, stranger, s.Document, new CreateShareLinkRequest(Guid.NewGuid(), null, null, null), Now, Ct);
        var revoke = await ShareService.RevokeAsync(db, stranger, link.Id, Now, Ct);
        var list = await ShareService.ListAsync(db, stranger, s.Document, Now, Ct);

        // Assert
        authorize.Outcome.Should().Be(ShareOutcome.NotFound);
        create.Outcome.Should().Be(ShareOutcome.NotFound);
        revoke.Should().Be(ShareOutcome.NotFound);
        list.Should().BeNull();
        (await s.OpenAsync(link.Token, link.Password, Ct)).Outcome.Should().Be(PublicOpenOutcome.Opened);
    }

    [Fact]
    public async Task RevokeAsync_TakesEffectAtOnce_AndAuditsOnce()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);
        await using var db = s.Db.CreateDbContext();

        // Act
        var first = await ShareService.RevokeAsync(db, s.Provider, link.Id, Now, Ct);
        var second = await ShareService.RevokeAsync(db, s.Provider, link.Id, Now, Ct);
        var opened = await s.OpenAsync(link.Token, link.Password, Ct);

        // Assert
        first.Should().Be(ShareOutcome.Ok);
        second.Should().Be(ShareOutcome.Ok);
        opened.Outcome.Should().Be(PublicOpenOutcome.Unavailable);
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == ShareService.LinkRevokedAction, Ct);
        audit.NewValue.Should().Be(link.Id.ToString());
    }

    [Fact]
    public async Task ListAsync_ShowsStatusViewsAndAccesses()
    {
        // Arrange
        await using var s = await StartAsync();
        var active = await s.SharedAsync(Ct);
        var capped = await s.SharedAsync(Ct, cap: 1);
        var revoked = await s.SharedAsync(Ct);
        var expired = await s.SharedAsync(Ct, at: Now.AddDays(-8));
        await using var db = s.Db.CreateDbContext();
        await s.OpenAsync(active.Token, "errada", Ct);
        await s.OpenAsync(active.Token, active.Password, Ct);
        await s.OpenAsync(capped.Token, capped.Password, Ct);
        await ShareService.RevokeAsync(db, s.Provider, revoked.Id, Now, Ct);

        // Act
        var list = await ShareService.ListAsync(db, s.Provider, s.Document, Now, Ct);

        // Assert
        list!.CanShare.Should().BeTrue();
        var links = list.Authorizations.SelectMany(a => a.Links).ToDictionary(l => l.Id);
        links[active.Id].Status.Should().Be("active");
        links[active.Id].ViewsUsed.Should().Be(1);
        links[active.Id].Accesses.Select(a => a.Outcome).Should().BeEquivalentTo("bad_password", "opened");
        links[active.Id].Accesses.Should().OnlyContain(a => a.UserAgent == "Agente de teste");
        links[capped.Id].Status.Should().Be("capped");
        links[revoked.Id].Status.Should().Be("revoked");
        links[expired.Id].Status.Should().Be("expired");
    }

    [Fact]
    public async Task ListAsync_ManyAccesses_ShowsTheNewestAndTheTotal()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct);
        await s.Db.ExecuteAsync(
            """INSERT INTO "ShareAccesses" ("ShareLinkId","Outcome","At") SELECT @l, 'blocked', @now - (n || ' minutes')::interval FROM generate_series(1, 150) n""",
            Ct, ("l", link.Id), ("now", Now));
        await using var db = s.Db.CreateDbContext();

        // Act
        var list = await ShareService.ListAsync(db, s.Provider, s.Document, Now, Ct);

        // Assert
        var item = list!.Authorizations.SelectMany(a => a.Links).Single();
        item.AccessCount.Should().Be(150);
        item.Accesses.Should().HaveCount(ShareService.MaxAccessesListed);
        item.Accesses[0].At.Should().Be(Now.AddMinutes(-1));
    }

    [Fact]
    public async Task OpenTermAsync_StreamsTheFileScopedAndAudited()
    {
        // Arrange
        await using var s = await StartAsync();
        var authorization = await s.AuthorizeAsync(Ct, term: new TermUpload("termo.pdf", Pdf));
        var stranger = await s.Db.AddProviderAsync(Ct, withCrp: true);
        await using var db = s.Db.CreateDbContext();

        // Act
        var opened = await ShareService.OpenTermAsync(db, s.Store, s.Provider, authorization.Id!.Value, Now, Ct);
        var foreign = await ShareService.OpenTermAsync(db, s.Store, stranger, authorization.Id!.Value, Now, Ct);

        // Assert
        opened.Outcome.Should().Be(ShareOutcome.Ok);
        opened.Content.Should().Equal(Pdf);
        opened.ContentType.Should().Be("application/pdf");
        foreign.Outcome.Should().Be(ShareOutcome.NotFound);
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == ShareService.TermOpenedAction, Ct);
        audit.DocumentId.Should().Be(s.Document);
        audit.NewValue.Should().Be(authorization.Id.ToString());
    }

    [Fact]
    public async Task OpenTermAsync_NoTermOrStoreDown_IsRefusedAndNotAudited()
    {
        // Arrange
        await using var s = await StartAsync();
        var bare = await s.AuthorizeAsync(Ct);
        var attached = await s.AuthorizeAsync(Ct, ShareScratch.Request("Outra Pessoa"), new TermUpload("termo.pdf", Pdf));
        s.Store.Objects.Clear();
        await using var db = s.Db.CreateDbContext();

        // Act
        var none = await ShareService.OpenTermAsync(db, s.Store, s.Provider, bare.Id!.Value, Now, Ct);
        var gone = await ShareService.OpenTermAsync(db, s.Store, s.Provider, attached.Id!.Value, Now, Ct);

        // Assert
        none.Outcome.Should().Be(ShareOutcome.NotFound);
        gone.Outcome.Should().Be(ShareOutcome.NotFound);
        (await db.AuditEntries.CountAsync(a => a.Action == ShareService.TermOpenedAction, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task CreateLinkAsync_NoSlotFree_IsBusyAndCreatesNothing()
    {
        // Arrange
        await using var s = await StartAsync();
        var authorization = await s.AuthorizeAsync(Ct);
        s.Limiter = new ShareAttemptLimiter(1, 0);
        using var held = await s.Limiter.AcquireAsync(Ct);

        // Act
        var result = await s.LinkAsync(authorization.Id!.Value, Ct);

        // Assert
        result.Outcome.Should().Be(ShareOutcome.Busy);
        await using var db = s.Db.CreateDbContext();
        (await db.ShareLinks.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task DiscardAsync_RemovesLinksAccessesAuthorizationsAndTheAttachedTerm()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var record = await DiscardServiceTests.SeedExpiredAsync(s, store);
        var document = record.Documents[0];
        var authorizationId = Guid.NewGuid();
        var termKey = ShareService.TermKeyFor(record.Provider, authorizationId);
        store.Objects[termKey] = [1];
        await s.ExecuteAsync(
            """
            INSERT INTO "DisclosureAuthorizations" ("Id","DocumentId","PatientId","ProviderId","RecipientName","RecipientRole","Purpose","AuthorizedAt","RecordedBy","ObtainedVia","TermObjectKey","ConfirmedAt")
            VALUES (@a, @d, @pa, @p, 'Dr. Pedro', 'Psiquiatra', 'Cuidado', now(), @p, 'signed_term', @key, now());
            WITH link AS (
                INSERT INTO "ShareLinks" ("DocumentId","AuthorizationId","TokenHash","PasswordHash","ExpiresAt")
                VALUES (@d, @a, 'hash', 'senha', now() + interval '1 day') RETURNING "Id")
            INSERT INTO "ShareAccesses" ("ShareLinkId","Outcome") SELECT "Id", 'opened' FROM link;
            """, Ct, ("a", authorizationId), ("d", document), ("pa", record.Patient), ("p", record.Provider), ("key", termKey));
        await using var db = s.CreateDbContext();

        // Act
        var result = await DiscardService.DiscardAsync(db, store, NullLogger.Instance, record.Provider, record.Patient, "Ana Silva", new DateTimeOffset(2026, 10, 27, 15, 0, 0, TimeSpan.Zero), Ct);

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.Ok);
        result.Counts!["shareLinks"].Should().Be(1);
        result.Counts["disclosureAuthorizations"].Should().Be(1);
        (await s.CountAsync("""SELECT count(*) FROM "ShareLinks" """, Ct)).Should().Be(0);
        (await s.CountAsync("""SELECT count(*) FROM "ShareAccesses" """, Ct)).Should().Be(0);
        (await s.CountAsync("""SELECT count(*) FROM "DisclosureAuthorizations" """, Ct)).Should().Be(0);
        store.Objects.Should().NotContainKey(termKey);
    }
}
