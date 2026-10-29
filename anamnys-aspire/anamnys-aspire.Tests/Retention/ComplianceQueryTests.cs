using Anamnys.Server.Documents;
using Anamnys.Server.Retention;
using FluentAssertions;

namespace Anamnys.Tests.Retention;

// The compliance panel (design/specs/2026-10-28-retention-panel-design.md §3, §6 "Panel").
[Collection(SharedAppHostCollection.Name)]
public class ComplianceQueryTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 27);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetAsync_UnsignedNotes_FromTheThreshold_OldestFirst_ScopedAndWithoutDiscarded()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var other = await s.AddProviderAsync(Ct);
        var ana = await s.AddPatientAsync(provider, "Ana", Ct);
        var gone = await s.AddPatientAsync(provider, "Bia", Ct);
        var foreign = await s.AddPatientAsync(other, "Cid", Ct);
        var tenDays = await s.AddNoteAsync(provider, ana, Now.AddDays(-10), Ct);
        // 23:30 local on 20 Oct: seven practice days old today, though less than seven times 24 hours.
        var sevenDays = await s.AddNoteAsync(provider, ana, new DateTimeOffset(2026, 10, 21, 2, 30, 0, TimeSpan.Zero), Ct, status: "Draft");
        await s.AddNoteAsync(provider, ana, Now.AddDays(-6), Ct);
        await s.AddNoteAsync(provider, ana, Now.AddDays(-30), Ct, signedAt: Now.AddDays(-29));
        await s.AddNoteAsync(provider, gone, Now.AddDays(-30), Ct);
        await s.MarkDiscardedAsync(gone, Now.AddDays(-1), Ct);
        await s.AddNoteAsync(other, foreign, Now.AddDays(-30), Ct);
        await using var db = s.CreateDbContext();

        // Act
        var panel = await ComplianceQuery.GetAsync(db, provider, Now, Ct);

        // Assert
        panel.UnsignedNotes.Select(n => (n.NoteId, n.AgeDays)).Should().Equal((tenDays, 10), (sevenDays, 7));
        panel.UnsignedNotes[0].PatientId.Should().Be(ana);
        panel.UnsignedNotes[0].PatientName.Should().Be("Ana Silva");
    }

    [Fact]
    public async Task GetAsync_UnsignedNotes_FollowTheProvidersThreshold()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var ana = await s.AddPatientAsync(provider, "Ana", Ct);
        var note = await s.AddNoteAsync(provider, ana, Now.AddDays(-2), Ct);
        await s.ExecuteAsync("""UPDATE "Providers" SET "UnsignedNoteDays" = 2 WHERE "Id" = @p""", Ct, ("p", provider));
        await using var db = s.CreateDbContext();

        // Act
        var panel = await ComplianceQuery.GetAsync(db, provider, Now, Ct);

        // Assert
        panel.UnsignedNotes.Select(n => n.NoteId).Should().Equal(note);
    }

    [Fact]
    public async Task GetAsync_Retention_Within90DaysOrExpired_ByDate_WithFutureAppointments()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var other = await s.AddProviderAsync(Ct);
        var soon = await s.AddPatientAsync(provider, "Ana", Ct);
        var edge = await s.AddPatientAsync(provider, "Bia", Ct);
        var later = await s.AddPatientAsync(provider, "Cid", Ct);
        var expired = await s.AddPatientAsync(provider, "Dan", Ct);
        var today = await s.AddPatientAsync(provider, "Eva", Ct);
        var none = await s.AddPatientAsync(provider, "Fia", Ct);
        var gone = await s.AddPatientAsync(provider, "Gil", Ct, discardedAt: Now.AddDays(-1));
        var foreign = await s.AddPatientAsync(other, "Hal", Ct);
        await s.SetRetentionAsync(soon, new DateOnly(2021, 11, 26), new DateOnly(2026, 11, 26), Ct);
        await s.SetRetentionAsync(edge, new DateOnly(2022, 1, 25), Today.AddDays(90), Ct);
        await s.SetRetentionAsync(later, new DateOnly(2022, 1, 26), Today.AddDays(91), Ct);
        await s.SetRetentionAsync(expired, new DateOnly(2020, 1, 1), new DateOnly(2025, 1, 1), Ct);
        await s.SetRetentionAsync(today, new DateOnly(2021, 10, 27), Today, Ct);
        await s.SetRetentionAsync(gone, new DateOnly(2019, 1, 1), new DateOnly(2024, 1, 1), Ct);
        await s.SetRetentionAsync(foreign, new DateOnly(2019, 1, 1), new DateOnly(2024, 1, 1), Ct);
        await s.AddAppointmentAsync(provider, soon, Now.AddDays(3), "scheduled", Ct);
        await s.AddAppointmentAsync(provider, expired, Now.AddDays(3), "cancelled", Ct);
        await s.AddAppointmentAsync(provider, expired, Now.AddDays(-3), "attended", Ct);
        await using var db = s.CreateDbContext();

        // Act
        var panel = await ComplianceQuery.GetAsync(db, provider, Now, Ct);

        // Assert
        panel.Retention.Select(r => (r.PatientId, r.Expired, r.HasFutureAppointment)).Should().Equal(
            (expired, true, false), (today, false, false), (soon, false, true), (edge, false, false));
        panel.Retention[0].Should().Be(new RetentionItem(expired, "Dan Silva", new DateOnly(2020, 1, 1), new DateOnly(2025, 1, 1), true, false));
        _ = none;
    }

    [Fact]
    public async Task GetAsync_Documents_DraftsAndSignaturesWithMissingElements_ByDate()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct, withCrp: true, checkMode: "advisory");
        var other = await s.AddProviderAsync(Ct, withCrp: true);
        var ana = await s.AddPatientAsync(provider, "Ana", Ct);
        var foreign = await s.AddPatientAsync(other, "Bia", Ct);
        await using var db = s.CreateDbContext();
        var incomplete = (await ClinicalDocumentService.CreateAsync(db, provider, ana, "declaracao", null, Now.AddDays(-5), Ct)).Id!.Value;
        await ConfirmAllAsync(s, incomplete);
        (await ClinicalDocumentService.SignAsync(db, provider, incomplete, acknowledgeMissing: true, Now.AddDays(-4), Ct)).Outcome.Should().Be(DocumentOutcome.Ok);
        var draft = (await ClinicalDocumentService.CreateAsync(db, provider, ana, "atestado", null, Now.AddDays(-2), Ct)).Id!.Value;
        await s.AddSignedDocumentAsync(provider, ana, Now.AddDays(-9), Ct);
        await ClinicalDocumentService.CreateAsync(db, other, foreign, "declaracao", null, Now.AddDays(-1), Ct);

        // Act
        var panel = await ComplianceQuery.GetAsync(db, provider, Now, Ct);

        // Assert
        panel.Documents.Select(d => (d.DocumentId, d.Status)).Should().Equal((incomplete, "signed"), (draft, "draft"));
        panel.Documents.Should().AllSatisfy(d => d.Missing.Should().NotBeEmpty());
        panel.Documents[0].Kind.Should().Be("declaracao");
        panel.Documents[0].PatientName.Should().Be("Ana Silva");
    }

    private static Task ConfirmAllAsync(RetentionScratch s, Guid documentId) =>
        s.ExecuteAsync(
            """UPDATE "ClinicalDocuments" d SET "Confirmations" = r."Prohibitions" FROM "ConformityRulesets" r WHERE r."Id" = d."RulesetId" AND d."Id" = @d""",
            Ct, ("d", documentId));
}
