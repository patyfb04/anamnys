using Anamnys.Server.Documents;
using Anamnys.Server.Sharing;
using FluentAssertions;
using QuestPDF.Infrastructure;

namespace Anamnys.Tests.Sharing;

// The provider's overview of every link (GET providers/me/shares): scoping, the status filter, paging and counts.
[Collection(SharedAppHostCollection.Name)]
public class ShareOverviewTests(SharedAppHostFixture fixture)
{
    private static DateTimeOffset Now => ShareScratch.Now;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ShareOverviewTests() => QuestPDF.Settings.License = LicenseType.Community;

    private Task<ShareScratch> StartAsync() => ShareScratch.CreateAsync(fixture, Ct);

    // A second patient with a signed document, on the same scratch database; the owner is disposed by the caller of StartAsync.
    private static async Task<ShareScratch> OtherAsync(ShareScratch s, Guid provider, bool discard = false)
    {
        var patient = await s.Db.AddPatientAsync(provider, "Bia", Ct);
        var document = await s.Db.AddSignedDocumentAsync(provider, patient, Now.AddDays(-3), Ct);
        var other = new ShareScratch(s.Db, provider, patient, document);
        if (discard)
        {
            await other.SharedAsync(Ct);
            await s.Db.ExecuteAsync("""UPDATE "Patients" SET "DiscardedAt" = now() WHERE "Id" = @p""", Ct, ("p", patient));
        }
        return other;
    }

    [Fact]
    public async Task ListOverviewAsync_OnlyTheProvidersLinks_WithoutDiscardedPatients()
    {
        // Arrange
        await using var s = await StartAsync();
        var mine = await s.SharedAsync(Ct);
        var stranger = await s.Db.AddProviderAsync(Ct, withCrp: true);
        var other = await OtherAsync(s, stranger);
        await other.SharedAsync(Ct);
        await OtherAsync(s, s.Provider, discard: true);
        await using var db = s.Db.CreateDbContext();

        // Act
        var page = await ShareService.ListOverviewAsync(db, s.Provider, false, 1, Now, Ct);

        // Assert
        page.Total.Should().Be(1);
        var item = page.Items.Should().ContainSingle().Subject;
        item.LinkId.Should().Be(mine.Id);
        item.DocumentId.Should().Be(s.Document);
        item.PatientId.Should().Be(s.Patient);
        item.PatientName.Should().Be("Ana Silva");
        item.DocumentKind.Should().Be("declaracao");
        item.RecipientName.Should().Be("Dr. Pedro Lima");
        item.RecipientRole.Should().Be("Psiquiatra");
        item.NewerVersionExists.Should().BeFalse();
        (await ShareService.ListOverviewAsync(db, stranger, false, 1, Now, Ct)).Items.Should().ContainSingle().Which.PatientName.Should().Be("Bia Silva");
    }

    [Fact]
    public async Task ListOverviewAsync_ActiveFilter_KeepsOnlyLinksThatOpen()
    {
        // Arrange
        await using var s = await StartAsync();
        var active = await s.SharedAsync(Ct);
        var capped = await s.SharedAsync(Ct, cap: 1);
        var revoked = await s.SharedAsync(Ct);
        var expired = await s.SharedAsync(Ct, at: Now.AddDays(-8));
        var blocked = await s.SharedAsync(Ct);
        await using var db = s.Db.CreateDbContext();
        await s.OpenAsync(capped.Token, capped.Password, Ct);
        await ShareService.RevokeAsync(db, s.Provider, revoked.Id, Now, Ct);
        for (var i = 0; i < ShareService.MaxWrongPasswords; i++)
        {
            await s.OpenAsync(blocked.Token, "errada", Ct);
        }

        // Act
        var onlyActive = await ShareService.ListOverviewAsync(db, s.Provider, true, 1, Now, Ct);
        var all = await ShareService.ListOverviewAsync(db, s.Provider, false, 1, Now, Ct);

        // Assert
        onlyActive.Items.Select(i => i.LinkId).Should().BeEquivalentTo([active.Id]);
        onlyActive.Total.Should().Be(1);
        all.Total.Should().Be(5);
        all.Items.ToDictionary(i => i.LinkId, i => i.Status).Should().BeEquivalentTo(new Dictionary<Guid, string>
        {
            [active.Id] = "active", [capped.Id] = "capped", [revoked.Id] = "revoked", [expired.Id] = "expired", [blocked.Id] = "blocked",
        });
    }

    [Fact]
    public async Task ListOverviewAsync_PatientFilter_KeepsOnlyThatPatientsLinks()
    {
        // Arrange
        await using var s = await StartAsync();
        var mine = await s.SharedAsync(Ct);
        var second = await OtherAsync(s, s.Provider);
        var secondLink = await second.SharedAsync(Ct);
        await using var db = s.Db.CreateDbContext();

        // Act
        var first = await ShareService.ListOverviewAsync(db, s.Provider, false, 1, Now, Ct, s.Patient);
        var both = await ShareService.ListOverviewAsync(db, s.Provider, false, 1, Now, Ct);

        // Assert
        first.Items.Select(i => i.LinkId).Should().BeEquivalentTo([mine.Id]);
        first.Total.Should().Be(1);
        both.Total.Should().Be(2);
        both.Items.Select(i => i.LinkId).Should().Contain(secondLink.Id);
    }

    [Fact]
    public async Task ListOverviewAsync_Paging_NewestFirst_TwentyPerPage()
    {
        // Arrange
        await using var s = await StartAsync();
        var authorization = (await s.AuthorizeAsync(Ct)).Id!.Value;
        var created = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            created.Add((await s.LinkAsync(authorization, Ct, at: Now.AddMinutes(i))).Link!.Id);
        }
        await using var db = s.Db.CreateDbContext();

        // Act
        var first = await ShareService.ListOverviewAsync(db, s.Provider, false, 1, Now.AddHours(1), Ct);
        var second = await ShareService.ListOverviewAsync(db, s.Provider, false, 2, Now.AddHours(1), Ct);
        var beyond = await ShareService.ListOverviewAsync(db, s.Provider, false, 3, Now.AddHours(1), Ct);

        // Assert
        first.Total.Should().Be(25);
        first.PageSize.Should().Be(20);
        first.Items.Select(i => i.LinkId).Should().Equal(Enumerable.Reverse(created).Take(20));
        second.Items.Select(i => i.LinkId).Should().Equal(Enumerable.Reverse(created).Skip(20));
        beyond.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListOverviewAsync_CountsAccessesViewsAndNewerVersion()
    {
        // Arrange
        await using var s = await StartAsync();
        var link = await s.SharedAsync(Ct, cap: 3);
        var untouched = await s.SharedAsync(Ct);
        await s.OpenAsync(link.Token, "errada", Ct, at: Now);
        await s.OpenAsync(link.Token, link.Password, Ct, at: Now.AddMinutes(5));
        await using var db = s.Db.CreateDbContext();
        await ClinicalDocumentService.CorrectAsync(db, s.Provider, s.Document, Now, Ct);

        // Act
        var page = await ShareService.ListOverviewAsync(db, s.Provider, false, 1, Now.AddMinutes(10), Ct);

        // Assert
        var item = page.Items.Single(i => i.LinkId == link.Id);
        item.AccessCount.Should().Be(2);
        item.LastAccessAt.Should().Be(Now.AddMinutes(5));
        item.ViewsUsed.Should().Be(1);
        item.ViewCap.Should().Be(3);
        item.NewerVersionExists.Should().BeTrue();
        var none = page.Items.Single(i => i.LinkId == untouched.Id);
        none.AccessCount.Should().Be(0);
        none.LastAccessAt.Should().BeNull();
    }
}
