using System.Security.Cryptography;
using System.Text;
using Anamnys.Server.Notifications;
using Anamnys.Server.Patients;
using Anamnys.Server.RecordingConsents;
using Anamnys.Server.Retention;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Notifications;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;
using static Anamnys.Tests.RecordingConsents.ConsentScene;

namespace Anamnys.Tests.RecordingConsents;

// design/specs/2026-11-15-recording-consent-design.md §3–§5, §7: sending, the signing link, the minor's assent, the
// expiries, revocation, renewal, scoping and the discard.
[Collection(SharedAppHostCollection.Name)]
public class RecordingConsentServiceTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static RecordingConsentServiceTests() => QuestPDF.Settings.License = LicenseType.Community;

    private static PublicConsentSignRequest AdultSignature(DateOnly? birth = null) => new(birth ?? AdultBirth, "Benedita Silva", null, true);

    [Fact]
    public async Task SendAsync_EachMissingPrecondition_IsRefusedWithItsOutcome_AndNothingIsWritten()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var noCrp = await s.Db.AddProviderAsync(Ct);
        var noCrpPatient = await s.Db.AddPatientAsync(noCrp, "Dora", Ct);
        await s.SetContactAsync(noCrpPatient, "dora@example.test", AdultBirth, Ct);
        var noEmail = await s.Db.AddPatientAsync(s.Provider, "Edu", Ct);
        await s.SetContactAsync(noEmail, null, AdultBirth, Ct);
        var noBirth = await s.Db.AddPatientAsync(s.Provider, "Fabi", Ct);
        await s.SetContactAsync(noBirth, "fabi@example.test", null, Ct);
        var archived = await s.Db.AddPatientAsync(s.Provider, "Gil", Ct);
        await s.SetContactAsync(archived, "gil@example.test", AdultBirth, Ct);
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @pa""", Ct, ("pa", archived));

        // Act
        var profile = await s.SendAsync(noCrpPatient, Ct, provider: noCrp);
        var email = await s.SendAsync(noEmail, Ct);
        var birth = await s.SendAsync(noBirth, Ct);
        var isArchived = await s.SendAsync(archived, Ct);
        var stranger = await s.SendAsync(s.Stranger, Ct);
        var tooLong = await s.SendAsync(s.Adult, Ct, note: new string('a', 1001));
        await using var db = s.CreateDbContext();
        var noSender = await RecordingConsentService.SendAsync(db, null, Portal, s.Provider, s.Adult, null, Now, Ct);

        // Assert
        profile.Outcome.Should().Be(ConsentOutcome.ProfileIncomplete);
        email.Outcome.Should().Be(ConsentOutcome.PatientEmailMissing);
        birth.Outcome.Should().Be(ConsentOutcome.PatientBirthDateMissing);
        isArchived.Outcome.Should().Be(ConsentOutcome.PatientArchived);
        stranger.Outcome.Should().Be(ConsentOutcome.NotFound);
        tooLong.Outcome.Should().Be(ConsentOutcome.InvalidNote);
        noSender.Outcome.Should().Be(ConsentOutcome.EmailNotConfigured);
        (await s.Db.CountAsync("""SELECT count(*) FROM "RecordingConsents" """, Ct)).Should().Be(0);
        s.Sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_StoresOnlyTheTokenHash_EmailsTheLinkWithoutClinicalContent_AndRefusesASecondActive()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);

        // Act
        var sent = await s.SendAsync(s.Adult, Ct, note: "Sessões às terças.");
        var again = await s.SendAsync(s.Adult, Ct);
        var token = s.SignToken();

        // Assert
        sent.Outcome.Should().Be(ConsentOutcome.Ok);
        again.Outcome.Should().Be(ConsentOutcome.ConsentActive);
        var view = sent.View!;
        (view.State, view.Version, view.CurrentVersion, view.CanRecord, view.Reason).Should().Be(("sent", "v1", "v1", false, "sent"));
        view.LinkExpiresAt.Should().Be(Now.AddDays(7));
        view.Events.Select(e => $"{e.FromState}>{e.ToState} {e.ActorKind}").Should().Equal("not_requested>sent provider");

        await using var db = s.CreateDbContext();
        var consent = await db.RecordingConsents.AsNoTracking().SingleAsync(Ct);
        consent.SignTokenHash.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        consent.ProviderId.Should().Be(s.Provider);
        consent.TreatmentPlanId.Should().BeNull();
        consent.TermText.Should().Contain("Benedita Silva").And.Contain("Sessões às terças.").And.NotContain(token);
        consent.TermSha256.Should().Be(ConsentTerm.Sha256(consent.TermText!));

        var message = s.Sender.Sent.Should().ContainSingle().Subject;
        message.ToEmail.Should().Be("benedita@example.test");
        message.Text.Should().Contain($"https://portal.test/patient/consentimento/{token}").And.Contain("Dra. Teste")
            .And.NotContain("04/05/1990").And.NotContain("Sessões às terças.");
        (await s.AuditAsync(Ct)).Should().Equal($"consent_sent {consent.Id} sent");
    }

    [Fact]
    public async Task SendAsync_EmailFails_LeavesNoConsent()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var failing = new RecordingEmailSender { FailNext = 1 };

        // Act
        var result = await s.SendAsync(s.Adult, Ct, sender: failing);
        var view = await s.ViewAsync(s.Adult, Ct);

        // Assert
        result.Outcome.Should().Be(ConsentOutcome.SendFailed);
        view!.State.Should().Be("none");
        (await s.Db.CountAsync("""SELECT count(*) FROM "AuditEntries" WHERE "Action" = 'consent_sent'""", Ct)).Should().Be(0);
    }

    [Fact]
    public async Task SignAsync_WrongBirthDate_IsCounted_TheFifthKillsTheLink_AndEveryDeadLinkAnswersGone()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SendAsync(s.Adult, Ct);
        var token = s.SignToken();
        var wrong = AdultSignature(new DateOnly(1991, 1, 1));

        // Act
        var first = new List<PublicConsentOutcome>();
        for (var i = 0; i < 4; i++)
        {
            first.Add((await s.SignAsync(token, wrong, Ct)).Outcome);
        }
        var fifth = await s.SignAsync(token, wrong, Ct);
        var right = await s.SignAsync(token, AdultSignature(), Ct);
        await using var db = s.CreateDbContext();
        var view = await RecordingConsentService.ViewSignAsync(db, token, Now, Ct);
        var unknown = await s.SignAsync(new string('A', 43), AdultSignature(), Ct);
        var malformed = await s.SignAsync("short", AdultSignature(), Ct);

        // Assert
        first.Should().AllBeEquivalentTo(PublicConsentOutcome.BirthDateMismatch);
        fifth.Outcome.Should().Be(PublicConsentOutcome.Gone);
        right.Outcome.Should().Be(PublicConsentOutcome.Gone);
        view.Should().BeNull();
        unknown.Outcome.Should().Be(PublicConsentOutcome.Gone);
        malformed.Outcome.Should().Be(PublicConsentOutcome.Gone);
        (await s.EffectiveAsync(s.Adult, Ct))!.FailedBirthDateCount.Should().Be(5);
    }

    [Fact]
    public async Task SignAsync_IncompleteBody_IsInvalid_AndTheViewHidesTheBirthDate()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SendAsync(s.Minor, Ct);
        var token = s.SignToken();

        // Act
        await using var db = s.CreateDbContext();
        var view = await RecordingConsentService.ViewSignAsync(db, token, Now, Ct);
        var noRelationship = await s.SignAsync(token, new PublicConsentSignRequest(MinorBirth, "Joana Souza", null, true), Ct);
        var notAccepted = await s.SignAsync(token, new PublicConsentSignRequest(MinorBirth, "Joana Souza", "mãe", false), Ct);

        // Assert
        view!.GuardianExpected.Should().BeTrue();
        view.ProviderName.Should().Be("Dra. Teste");
        view.Text.Should().Contain("Caio Silva").And.NotContain("10/03/2012");
        noRelationship.Outcome.Should().Be(PublicConsentOutcome.Invalid);
        noRelationship.Errors.Should().Equal("relationship_required");
        notAccepted.Errors.Should().Equal("accepted_required");
    }

    [Fact]
    public async Task SignAsync_RightBirthDate_SignsStoresThePdfEmailsTheCopyAndRingsTheBell_AndTheLinkIsOneTime()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SendAsync(s.Adult, Ct);
        var token = s.SignToken();

        // Act
        var signed = await s.SignAsync(token, AdultSignature(), Ct);
        var again = await s.SignAsync(token, AdultSignature(), Ct);
        var view = await s.ViewAsync(s.Adult, Ct);
        await using var db = s.CreateDbContext();
        var inbox = await ProviderNotifications.ListAsync(db, s.Provider, 1, Ct);
        var revokeView = await RecordingConsentService.ViewRevokeAsync(db, s.RevokeToken(), Now, Ct);
        var document = await RecordingConsentService.DocumentAsync(db, s.Store, NullLogger.Instance, s.Provider, view!.Id!.Value, Ct);

        // Assert
        signed.Outcome.Should().Be(PublicConsentOutcome.Ok);
        again.Outcome.Should().Be(PublicConsentOutcome.Gone);
        (view.State, view.SignerRole, view.SignerName, view.CanRecord, view.Reason, view.HasDocument)
            .Should().Be(("signed", "patient", "Benedita Silva", true, "ok", true));
        view.Events.Select(e => $"{e.FromState}>{e.ToState} {e.ActorKind} {e.Reason}").Should().Equal(
            "not_requested>sent provider ", "sent>signed signer patient");

        var consent = await db.RecordingConsents.AsNoTracking().SingleAsync(Ct);
        consent.SignTokenHash.Should().BeNull();
        consent.RevokeTokenHash.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(s.RevokeToken())));
        consent.TermText.Should().Contain("04/05/1990").And.Contain("Assinado eletronicamente por Benedita Silva, paciente.")
            .And.Contain("São Paulo, 15/11/2026 às 12:01");
        consent.TermSha256.Should().Be(ConsentTerm.Sha256(consent.TermText!));
        consent.SignatureEvidence.Should().Contain("\"birthDateMatched\":true").And.Contain(consent.TermSha256);
        consent.DocumentObjectKey.Should().Be($"recording-consents/{s.Provider}/{consent.Id}.pdf");
        Encoding.ASCII.GetString(s.Store.Objects[consent.DocumentObjectKey!][..5]).Should().Be("%PDF-");
        document.Outcome.Should().Be(ConsentOutcome.Ok);
        document.FileName.Should().StartWith("termo-consentimento-gravacao-");

        var copy = s.Sender.Sent.Last();
        copy.Subject.Should().Be(ConsentEmails.SignedSubject);
        copy.Text.Should().Contain("/consentimento/revogar/").And.NotContain("04/05/1990");
        revokeView!.Text.Should().NotContain("04/05/1990").And.Contain("Benedita Silva");
        revokeView.TermSha256.Should().Be(consent.TermSha256);

        inbox.Items.Should().ContainSingle(i => i.Kind == RecordingConsentService.SignedKind && i.PatientId == s.Adult
            && i.PatientName == "Benedita Silva");
        // The download above is audited with the consent id only (its timestamp is the real clock, so the order is not asserted).
        (await s.AuditAsync(Ct)).Should().BeEquivalentTo(
            new[] { $"consent_sent {consent.Id} sent", $"consent_signed {consent.Id} patient", $"consent_document_downloaded {consent.Id}" });
    }

    [Fact]
    public async Task Document_IsAuditedOnlyWhenTheSignedPdfIsReturned()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var sent = await s.SendAsync(s.Adult, Ct);
        var (signedId, _) = await s.SignedAsync(s.Minor, Ct);

        // Act
        await using var db = s.CreateDbContext();
        var notSigned = await RecordingConsentService.DocumentAsync(db, s.Store, NullLogger.Instance, s.Provider, sent.View!.Id!.Value, Ct);
        var stranger = await RecordingConsentService.DocumentAsync(db, s.Store, NullLogger.Instance, s.OtherProvider, signedId, Ct);
        var downloaded = await RecordingConsentService.DocumentAsync(db, s.Store, NullLogger.Instance, s.Provider, signedId, Ct);
        var audit = (await s.AuditAsync(Ct)).Where(a => a.StartsWith("consent_document_downloaded")).ToList();

        // Assert
        (notSigned.Outcome, stranger.Outcome, downloaded.Outcome).Should().Be((ConsentOutcome.NotFound, ConsentOutcome.NotFound, ConsentOutcome.Ok));
        audit.Should().Equal($"consent_document_downloaded {signedId}");
    }

    [Fact]
    public async Task Resend_OfAnOlderTermVersion_IsRefusedAsOutdated()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var sent = await s.SendAsync(s.Adult, Ct);
        var id = sent.View!.Id!.Value;
        await s.Db.ExecuteAsync("""UPDATE "RecordingConsents" SET "TemplateVersion" = 'v0' WHERE "Id" = @c""", Ct, ("c", id));
        var emailsBefore = s.Sender.Sent.Count;

        // Act
        await using var db = s.CreateDbContext();
        var whileSent = await RecordingConsentService.ResendAsync(db, s.Sender, Portal, s.Provider, id, Now, Ct);
        await s.Db.ExecuteAsync(
            """UPDATE "RecordingConsents" SET "State" = 'EXPIRED', "ExpiredReason" = 'link' WHERE "Id" = @c""", Ct, ("c", id));
        var linkExpired = await RecordingConsentService.ResendAsync(db, s.Sender, Portal, s.Provider, id, Later, Ct);
        var row = await db.RecordingConsents.AsNoTracking().SingleAsync(c => c.Id == id, Ct);

        // Assert
        (whileSent.Outcome, linkExpired.Outcome).Should().Be((ConsentOutcome.OutdatedVersion, ConsentOutcome.OutdatedVersion));
        (row.State, row.TemplateVersion).Should().Be(("EXPIRED", "v0"));
        s.Sender.Sent.Count.Should().Be(emailsBefore);
    }

    [Fact]
    public async Task ExpiredLink_IsGone_ExpiresLazily_AndResendSendsANewLink()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var sent = await s.SendAsync(s.Adult, Ct);
        var oldToken = s.SignToken();
        var later = Now.AddDays(8);

        // Act
        var late = await s.SignAsync(oldToken, AdultSignature(), Ct, later);
        var expired = await s.EffectiveAsync(s.Adult, Ct, later);
        var view = await s.ViewAsync(s.Adult, Ct, later);
        await using var db = s.CreateDbContext();
        var resent = await RecordingConsentService.ResendAsync(db, s.Sender, Portal, s.Provider, sent.View!.Id!.Value, later, Ct);
        var newToken = s.SignToken();
        var oldAfter = await s.SignAsync(oldToken, AdultSignature(), Ct, later);
        var newSigned = await s.SignAsync(newToken, AdultSignature(), Ct, later.AddMinutes(1));

        // Assert
        late.Outcome.Should().Be(PublicConsentOutcome.Gone);
        (expired!.State, expired.ExpiredReason).Should().Be(("EXPIRED", "link"));
        (view!.State, view.Reason, view.LinkExpiresAt).Should().Be(("expired", "expired", null));
        resent.Outcome.Should().Be(ConsentOutcome.Ok);
        resent.View!.LinkExpiresAt.Should().Be(later.AddDays(7));
        newToken.Should().NotBe(oldToken);
        oldAfter.Outcome.Should().Be(PublicConsentOutcome.Gone);
        newSigned.Outcome.Should().Be(PublicConsentOutcome.Ok);
        (await s.EventsAsync(sent.View.Id!.Value, Ct)).Should().Equal(
            "NOT_REQUESTED>SENT provider -", "SENT>EXPIRED system link", "EXPIRED>SENT provider resent", "SENT>SIGNED signer patient");
    }

    [Fact]
    public async Task Minor_GuardianSigns_CaptureNeedsTheAssent_AndTurning18Expires()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (consentId, _) = await s.SignedAsync(s.Minor, Ct);
        var (adultConsent, _) = await s.SignedAsync(s.Adult, Ct);

        // Act
        var beforeAssent = await s.ViewAsync(s.Minor, Ct);
        await using var db = s.CreateDbContext();
        var future = await RecordingConsentService.RecordAssentAsync(db, s.Provider, consentId, new(DateOnly.FromDateTime(Now.Date).AddDays(1), null), Now, Ct);
        var notMinor = await RecordingConsentService.RecordAssentAsync(db, s.Provider, adultConsent, new(new DateOnly(2026, 11, 15), null), Now, Ct);
        var assent = await RecordingConsentService.RecordAssentAsync(db, s.Provider, consentId, new(new DateOnly(2026, 11, 15), "Concordou."), Later, Ct);
        var eighteenth = new DateTimeOffset(2030, 3, 10, 15, 0, 0, TimeSpan.Zero);
        var grown = await s.ViewAsync(s.Minor, Ct, eighteenth);

        // Assert
        (beforeAssent!.SignerRole, beforeAssent.SignerName, beforeAssent.GuardianRelationship, beforeAssent.IsMinor, beforeAssent.CanRecord, beforeAssent.Reason)
            .Should().Be(("guardian", "Joana Souza", "mãe", true, false, "assent_missing"));
        future.Outcome.Should().Be(ConsentOutcome.InvalidDate);
        notMinor.Outcome.Should().Be(ConsentOutcome.NotMinor);
        assent.Outcome.Should().Be(ConsentOutcome.Ok);
        (assent.View!.AssentRecordedAt, assent.View.AssentNote, assent.View.CanRecord).Should().Be((new DateOnly(2026, 11, 15), "Concordou.", true));
        (grown!.State, grown.ExpiredReason, grown.CanRecord, grown.IsMinor).Should().Be(("expired", "adulthood", false, false));
        var consent = await db.RecordingConsents.AsNoTracking().SingleAsync(c => c.Id == consentId, Ct);
        consent.GuardianName.Should().Be("Joana Souza");
        consent.TermText.Should().Contain("Responsável legal: Joana Souza (mãe).");
    }

    [Fact]
    public async Task RevokeByLink_WrongBirthDateCounted_RightOneRevokes_RingsTheBell_AndLeavesSignedNotes()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var note = await s.Db.AddNoteAsync(s.Provider, s.Adult, Now.AddDays(-1), Ct, signedAt: Now.AddDays(-1));
        var (consentId, revokeToken) = await s.SignedAsync(s.Adult, Ct);

        // Act
        var missing = await s.RevokeByLinkAsync(revokeToken, null, Ct);
        var wrong = await s.RevokeByLinkAsync(revokeToken, new DateOnly(2000, 1, 1), Ct);
        var revoked = await s.RevokeByLinkAsync(revokeToken, AdultBirth, Ct);
        var again = await s.RevokeByLinkAsync(revokeToken, AdultBirth, Ct);
        var view = await s.ViewAsync(s.Adult, Ct);
        await using var db = s.CreateDbContext();
        var inbox = await ProviderNotifications.ListAsync(db, s.Provider, 1, Ct);

        // Assert
        missing.Outcome.Should().Be(PublicConsentOutcome.Invalid);
        wrong.Outcome.Should().Be(PublicConsentOutcome.BirthDateMismatch);
        revoked.Outcome.Should().Be(PublicConsentOutcome.Ok);
        again.Outcome.Should().Be(PublicConsentOutcome.Gone);
        (view!.State, view.RevokedBy, view.CanRecord, view.Reason).Should().Be(("revoked", "patient", false, "revoked"));
        inbox.Items.Should().Contain(i => i.Kind == RecordingConsentService.RevokedKind && i.PatientId == s.Adult);
        (await s.Db.CountAsync("""SELECT count(*) FROM "Notes" WHERE "Id" = @n AND "Status" = 'Signed'""", Ct, ("n", note))).Should().Be(1);
        (await s.EventsAsync(consentId, Ct)).Last().Should().Be("SIGNED>REVOKED signer patient");
    }

    [Fact]
    public async Task RevokeByProvider_KeepsTheReasonOnTheConsentOnly_NoBell_AndANewTermCanBeSent()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (consentId, revokeToken) = await s.SignedAsync(s.Adult, Ct);

        // Act
        await using var db = s.CreateDbContext();
        var revoked = await RecordingConsentService.RevokeAsync(db, s.Provider, consentId, "Paciente pediu em sessão.", Later, Ct);
        var twice = await RecordingConsentService.RevokeAsync(db, s.Provider, consentId, null, Later, Ct);
        var link = await s.RevokeByLinkAsync(revokeToken, AdultBirth, Ct);
        var inbox = await ProviderNotifications.ListAsync(db, s.Provider, 1, Ct);
        var resent = await s.SendAsync(s.Adult, Ct);

        // Assert
        revoked.Outcome.Should().Be(ConsentOutcome.Ok);
        (revoked.View!.State, revoked.View.RevokedBy, revoked.View.RevokeReason).Should().Be(("revoked", "provider", "Paciente pediu em sessão."));
        twice.Outcome.Should().Be(ConsentOutcome.InvalidState);
        link.Outcome.Should().Be(PublicConsentOutcome.Gone);
        inbox.Items.Should().NotContain(i => i.Kind == RecordingConsentService.RevokedKind);
        (await s.EventsAsync(consentId, Ct)).Last().Should().Be("SIGNED>REVOKED provider provider");
        (await s.AuditAsync(Ct)).Should().Contain($"consent_revoked {consentId} provider").And.NotContain(a => a.Contains("pediu"));
        resent.Outcome.Should().Be(ConsentOutcome.Ok);
    }

    [Fact]
    public async Task ArchivingThePatient_ExpiresTheConsentInTheSameSave()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (consentId, _) = await s.SignedAsync(s.Adult, Ct);

        // Act
        await using (var db = s.CreateDbContext())
        {
            (await PatientRecords.SetArchivedAsync(db, s.Provider, s.Adult, true, Later, Ct)).Should().BeTrue();
        }
        var patient = await s.PatientAsync(s.Adult, Ct);
        await using var check = s.CreateDbContext();
        var consent = await check.RecordingConsents.AsNoTracking().SingleAsync(c => c.Id == consentId, Ct);
        var rule = ConsentRules.CanRecordSession(consent, patient, Now);

        // Assert
        (consent.State, consent.ExpiredReason, consent.RevokeTokenHash).Should().Be(("EXPIRED", "discharge", (byte[]?)null));
        rule.Should().Be((false, "archived"));
        (await s.EventsAsync(consentId, Ct)).Last().Should().Be("SIGNED>EXPIRED provider discharge");
        (await s.AuditAsync(Ct)).Should().Contain($"consent_expired {consentId} discharge");
    }

    [Fact]
    public async Task Renew_OlderVersionExpiresAndSendsTheCurrent_CurrentVersionIsNotOutdated()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (current, _) = await s.SignedAsync(s.Adult, Ct);
        var (old, _) = await s.SignedAsync(s.Minor, Ct);
        await s.Db.ExecuteAsync("""UPDATE "RecordingConsents" SET "TemplateVersion" = 'v0' WHERE "Id" = @c""", Ct, ("c", old));

        // Act
        await using var db = s.CreateDbContext();
        var before = await s.ViewAsync(s.Minor, Ct);
        var notOutdated = await RecordingConsentService.RenewAsync(db, s.Sender, Portal, s.Provider, current, Later, Ct);
        var renewed = await RecordingConsentService.RenewAsync(db, s.Sender, Portal, s.Provider, old, Later, Ct);
        var oldRow = await db.RecordingConsents.AsNoTracking().SingleAsync(c => c.Id == old, Ct);

        // Assert
        (before!.Version, before.CurrentVersion, before.CanRecord).Should().Be(("v0", "v1", false));
        notOutdated.Outcome.Should().Be(ConsentOutcome.NotOutdated);
        renewed.Outcome.Should().Be(ConsentOutcome.Ok);
        (renewed.View!.State, renewed.View.Version).Should().Be(("sent", "v1"));
        renewed.View.Id.Should().NotBe(old);
        (oldRow.State, oldRow.ExpiredReason).Should().Be(("EXPIRED", "new_version"));
        s.Sender.Sent.Last().Subject.Should().Be(ConsentEmails.RequestSubject);
    }

    [Fact]
    public async Task AnotherProvidersPatientOrConsent_IsNotFound()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (consentId, _) = await s.SignedAsync(s.Adult, Ct);

        // Act
        await using var db = s.CreateDbContext();
        var view = await s.ViewAsync(s.Adult, Ct, provider: s.OtherProvider);
        var preview = await RecordingConsentService.PreviewAsync(db, s.OtherProvider, s.Adult, Now, Ct);
        var resend = await RecordingConsentService.ResendAsync(db, s.Sender, Portal, s.OtherProvider, consentId, Now, Ct);
        var revoke = await RecordingConsentService.RevokeAsync(db, s.OtherProvider, consentId, null, Now, Ct);
        var assent = await RecordingConsentService.RecordAssentAsync(db, s.OtherProvider, consentId, new(new DateOnly(2026, 11, 1), null), Now, Ct);
        var renew = await RecordingConsentService.RenewAsync(db, s.Sender, Portal, s.OtherProvider, consentId, Now, Ct);
        var document = await RecordingConsentService.DocumentAsync(db, s.Store, NullLogger.Instance, s.OtherProvider, consentId, Ct);
        var effective = await RecordingConsentService.GetEffectiveAsync(db, s.OtherProvider, s.Adult, Now, Ct);

        // Assert
        view.Should().BeNull();
        preview.Should().BeNull();
        effective.Should().BeNull();
        new[] { resend.Outcome, revoke.Outcome, assent.Outcome, renew.Outcome, document.Outcome }.Should().AllBeEquivalentTo(ConsentOutcome.NotFound);
        (await s.ViewAsync(s.Adult, Ct))!.State.Should().Be("signed");
    }

    [Fact]
    public async Task Discard_RemovesTheConsentsEventsBellAndSignedPdf()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new FakeNoteFileStore();
        var record = await DiscardServiceTests.SeedExpiredAsync(s, store);
        var consent = Guid.NewGuid();
        var key = $"recording-consents/{record.Provider}/{consent}.pdf";
        await s.ExecuteAsync(
            """
            INSERT INTO "RecordingConsents" ("Id","PatientId","ProviderId","State","TemplateVersion","SignedAt","SignatureEvidence","DocumentObjectKey")
            VALUES (@c, @pa, @p, 'SIGNED', 'v1', now(), '{}', @key);
            INSERT INTO "ConsentEvents" ("ConsentId","FromState","ToState","ActorKind") VALUES (@c, 'SENT', 'SIGNED', 'signer');
            INSERT INTO "Notifications" ("ProviderId","Kind","SubjectType","SubjectId","Channel","ScheduledFor","DeliveryStatus")
            VALUES (@p, 'consent_signed', 'recording_consent', @c, 'in_app', now(), 'sent');
            """, Ct, ("c", consent), ("pa", record.Patient), ("p", record.Provider), ("key", key));
        store.Objects[key] = [1, 2, 3];

        // Act
        await using var db = s.CreateDbContext();
        var result = await DiscardService.DiscardAsync(db, store, NullLogger.Instance, record.Provider, record.Patient, "Ana Silva", new DateTimeOffset(2026, 10, 27, 15, 0, 0, TimeSpan.Zero), Ct);

        // Assert
        result.Outcome.Should().Be(DiscardOutcome.Ok);
        (await s.CountAsync("""SELECT count(*) FROM "RecordingConsents" WHERE "Id" = @c""", Ct, ("c", consent))).Should().Be(0);
        (await s.CountAsync("""SELECT count(*) FROM "ConsentEvents" WHERE "ConsentId" = @c""", Ct, ("c", consent))).Should().Be(0);
        (await s.CountAsync("""SELECT count(*) FROM "Notifications" WHERE "SubjectId" = @c""", Ct, ("c", consent))).Should().Be(0);
        store.Deleted.Should().Contain(key);
        result.Counts!["recordingConsents"].Should().Be(1);
    }
}
