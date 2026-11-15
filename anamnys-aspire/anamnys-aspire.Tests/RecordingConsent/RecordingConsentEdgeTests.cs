using Anamnys.Server.RecordingConsents;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;
using static Anamnys.Tests.RecordingConsents.ConsentScene;

namespace Anamnys.Tests.RecordingConsents;

// The review's edge cases (fix round 1): dead links after resend, the revocation link's attempt limit, lazy discharge,
// refused actions on closed consents, the signed PDF's content, concurrent signatures, the assent's lower bound, the CRP
// region, and the signed text built from the text the signer was shown.
[Collection(SharedAppHostCollection.Name)]
public class RecordingConsentEdgeTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    static RecordingConsentEdgeTests() => QuestPDF.Settings.License = LicenseType.Community;

    private static PublicConsentSignRequest AdultSignature(DateOnly? birth = null) => new(birth ?? AdultBirth, "Benedita Silva", null, true);

    [Fact]
    public async Task ResendWhileSent_KillsThePreviousLink()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var sent = await s.SendAsync(s.Adult, Ct);
        var first = s.SignToken();

        // Act
        await using var db = s.CreateDbContext();
        var resent = await RecordingConsentService.ResendAsync(db, s.Sender, Portal, s.Provider, sent.View!.Id!.Value, Now, Ct);
        var second = s.SignToken();
        var oldView = await RecordingConsentService.ViewSignAsync(db, first, Now, Ct);
        var oldSign = await s.SignAsync(first, AdultSignature(), Ct);
        var newSign = await s.SignAsync(second, AdultSignature(), Ct);

        // Assert
        resent.Outcome.Should().Be(ConsentOutcome.Ok);
        second.Should().NotBe(first);
        oldView.Should().BeNull();
        oldSign.Outcome.Should().Be(PublicConsentOutcome.Gone);
        newSign.Outcome.Should().Be(PublicConsentOutcome.Ok);
    }

    [Fact]
    public async Task RevocationLink_FifthWrongBirthDate_KillsTheLink()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (_, token) = await s.SignedAsync(s.Adult, Ct);
        var wrong = new DateOnly(2001, 1, 1);

        // Act
        var outcomes = new List<PublicConsentOutcome>();
        for (var i = 0; i < 5; i++)
        {
            outcomes.Add((await s.RevokeByLinkAsync(token, wrong, Ct)).Outcome);
        }
        var right = await s.RevokeByLinkAsync(token, AdultBirth, Ct);
        await using var db = s.CreateDbContext();
        var view = await RecordingConsentService.ViewRevokeAsync(db, token, Now, Ct);

        // Assert
        outcomes.Should().Equal(
            PublicConsentOutcome.BirthDateMismatch, PublicConsentOutcome.BirthDateMismatch, PublicConsentOutcome.BirthDateMismatch,
            PublicConsentOutcome.BirthDateMismatch, PublicConsentOutcome.Gone);
        right.Outcome.Should().Be(PublicConsentOutcome.Gone);
        view.Should().BeNull();
        (await s.ViewAsync(s.Adult, Ct))!.State.Should().Be("signed");
    }

    [Fact]
    public async Task ArchivedOutsideTheArchiveRoute_IsExpiredAsDischargeOnRead()
    {
        // Arrange: the archive date is set directly, so only the lazy evaluation can expire the consent.
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (consentId, _) = await s.SignedAsync(s.Adult, Ct);
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "ArchivedAt" = @at WHERE "Id" = @pa""", Ct, ("pa", s.Adult), ("at", Later.UtcDateTime));

        // Act
        var effective = await s.EffectiveAsync(s.Adult, Ct, Later.AddMinutes(5));

        // Assert
        (effective!.State, effective.ExpiredReason).Should().Be(("EXPIRED", "discharge"));
        (await s.EventsAsync(consentId, Ct)).Last().Should().Be("SIGNED>EXPIRED system discharge");
        (await s.AuditAsync(Ct)).Should().Contain($"consent_expired {consentId} discharge");
        await using var db = s.CreateDbContext();
        (await db.ConsentEvents.AsNoTracking().Where(e => e.ConsentId == consentId && e.ToState == "EXPIRED").SingleAsync(Ct)).At.Should().Be(Later);
    }

    [Fact]
    public async Task ClosedConsents_RefuseAssentRevokeAndResend()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (revoked, _) = await s.SignedAsync(s.Minor, Ct);
        var (discharged, _) = await s.SignedAsync(s.Adult, Ct);
        await using var db = s.CreateDbContext();
        await RecordingConsentService.RevokeAsync(db, s.Provider, revoked, null, Later, Ct);
        await s.Db.ExecuteAsync("""UPDATE "RecordingConsents" SET "State" = 'EXPIRED', "ExpiredReason" = 'discharge' WHERE "Id" = @c""", Ct, ("c", discharged));
        var day = new DateOnly(2026, 11, 15);

        // Act
        var outcomes = new[]
        {
            (await RecordingConsentService.RecordAssentAsync(db, s.Provider, revoked, new(day, null), Later, Ct)).Outcome,
            (await RecordingConsentService.RevokeAsync(db, s.Provider, revoked, null, Later, Ct)).Outcome,
            (await RecordingConsentService.ResendAsync(db, s.Sender, Portal, s.Provider, revoked, Later, Ct)).Outcome,
            (await RecordingConsentService.RecordAssentAsync(db, s.Provider, discharged, new(day, null), Later, Ct)).Outcome,
            (await RecordingConsentService.RevokeAsync(db, s.Provider, discharged, null, Later, Ct)).Outcome,
            (await RecordingConsentService.ResendAsync(db, s.Sender, Portal, s.Provider, discharged, Later, Ct)).Outcome,
        };

        // Assert
        outcomes.Should().AllBeEquivalentTo(ConsentOutcome.InvalidState);
    }

    [Fact]
    public async Task SignedPdf_CarriesTheTextSignerRoleRelationshipAndHash()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (consentId, _) = await s.SignedAsync(s.Minor, Ct);
        await using var db = s.CreateDbContext();
        var consent = await db.RecordingConsents.AsNoTracking().SingleAsync(c => c.Id == consentId, Ct);

        // Act
        using var pdf = PdfDocument.Open(s.Store.Objects[consent.DocumentObjectKey!]);
        var text = string.Concat(pdf.GetPages().Select(p => p.Text)).Replace(" ", "");

        // Assert
        text.Should().Contain("JoanaSouza").And.Contain("Responsávellegal").And.Contain("mãe").And.Contain("CaioSilva")
            .And.Contain(consent.TermSha256!).And.Contain("Versãodotermo:v1");
    }

    [Fact]
    public async Task TwoSignaturesAtOnce_OnlyOneWins()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SendAsync(s.Adult, Ct);
        var token = s.SignToken();

        // Act
        var results = await Task.WhenAll(s.SignAsync(token, AdultSignature(), Ct), s.SignAsync(token, AdultSignature(), Ct));

        // Assert
        results.Select(r => r.Outcome).Should().BeEquivalentTo([PublicConsentOutcome.Ok, PublicConsentOutcome.Gone]);
        (await s.Db.CountAsync("""SELECT count(*) FROM "ConsentEvents" WHERE "ToState" = 'SIGNED'""", Ct)).Should().Be(1);
        (await s.Db.CountAsync("""SELECT count(*) FROM "Notifications" WHERE "Kind" = 'consent_signed'""", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Assent_BeforeTheConsentWasRequested_IsInvalidDate()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var (consentId, _) = await s.SignedAsync(s.Minor, Ct);

        // Act
        await using var db = s.CreateDbContext();
        var before = await RecordingConsentService.RecordAssentAsync(db, s.Provider, consentId, new(new DateOnly(2026, 11, 14), null), Later, Ct);
        var sameDay = await RecordingConsentService.RecordAssentAsync(db, s.Provider, consentId, new(new DateOnly(2026, 11, 15), null), Later, Ct);

        // Assert
        before.Outcome.Should().Be(ConsentOutcome.InvalidDate);
        sameDay.Outcome.Should().Be(ConsentOutcome.Ok);
    }

    [Fact]
    public async Task Send_ProviderWithoutCrpRegion_IsProfileIncomplete()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.Db.ExecuteAsync("""UPDATE "Providers" SET "CrpRegion" = '' WHERE "Id" = @p""", Ct, ("p", s.Provider));

        // Act
        var result = await s.SendAsync(s.Adult, Ct);

        // Assert
        result.Outcome.Should().Be(ConsentOutcome.ProfileIncomplete);
    }

    [Fact]
    public async Task Sign_AfterThePatientsBirthDateAndNameWereCorrected_SignsATextOfTheCurrentRecord()
    {
        // Arrange: sent with 04/05/1990, then the provider corrects the date of birth and the surname.
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SendAsync(s.Adult, Ct);
        var token = s.SignToken();
        var corrected = new DateOnly(1990, 5, 5);
        await s.Db.ExecuteAsync("""UPDATE "Patients" SET "DateOfBirth" = @b, "LastName" = 'Souza' WHERE "Id" = @pa""", Ct,
            ("pa", s.Adult), ("b", corrected));
        await using var db = s.CreateDbContext();

        // Act
        var shown = (await RecordingConsentService.ViewSignAsync(db, token, Now, Ct))!.Text;
        var oldDate = await s.SignAsync(token, AdultSignature(), Ct);
        var signed = await s.SignAsync(token, AdultSignature(corrected), Ct);
        var consent = await db.RecordingConsents.AsNoTracking().SingleAsync(Ct);

        // Assert
        shown.Should().Contain("Paciente: Benedita Souza, data de nascimento confirmada por quem assina.");
        oldDate.Outcome.Should().Be(PublicConsentOutcome.BirthDateMismatch);
        signed.Outcome.Should().Be(PublicConsentOutcome.Ok);
        consent.TermText.Should().Contain("Paciente: Benedita Souza, nascido(a) em 05/05/1990.").And.NotContain("04/05/1990")
            .And.NotContain("Paciente: Benedita Silva");
        consent.TermSha256.Should().Be(ConsentTerm.Sha256(consent.TermText!));
    }

    [Theory]
    [InlineData("Benedita\nSilva", null)]
    [InlineData("Benedita\r\nSilva", null)]
    [InlineData("Benedita​Silva", null)]
    [InlineData("Joana Souza", "mãe\n11. Data e local")]
    [InlineData("Joana Souza", "mã\u0007e")]
    public async Task Sign_SignerNameOrRelationshipOnMoreThanOneLine_IsInvalid(string signerName, string? relationship)
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var minor = relationship is not null;
        await s.SendAsync(minor ? s.Minor : s.Adult, Ct);
        var token = s.SignToken();

        // Act
        var result = await s.SignAsync(token, new PublicConsentSignRequest(minor ? MinorBirth : AdultBirth, signerName, relationship, true), Ct);
        var view = await s.ViewAsync(minor ? s.Minor : s.Adult, Ct);

        // Assert
        result.Outcome.Should().Be(PublicConsentOutcome.Invalid);
        result.Errors.Should().Equal(minor ? "relationship_single_line" : "signer_name_single_line");
        (view!.State, view.SignerName).Should().Be(("sent", null));
    }

    // A signature holding the consent's row lock when the patient is archived: the archive waits for it, then expires the
    // consent from SIGNED, so the history reads sent > signed > expired. Deterministic: the lock is taken before the
    // archive starts, so the archive either waits on it or (if slower) reads the committed SIGNED row; both give this order.
    [Fact]
    public async Task Archive_WhileASignatureHoldsTheLock_ExpiresTheConsentFromSigned()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        var consentId = (await s.SendAsync(s.Adult, Ct)).View!.Id!.Value;
        await using var signer = await s.Db.OpenAsync(Ct);
        await using var tx = await signer.BeginTransactionAsync(Ct);
        await using (var command = new Npgsql.NpgsqlCommand(
            """
            SELECT 1 FROM "RecordingConsents" WHERE "Id" = @c FOR UPDATE;
            UPDATE "RecordingConsents" SET "State" = 'SIGNED', "SignedAt" = @at, "SignatureEvidence" = '{}', "SignTokenHash" = NULL WHERE "Id" = @c;
            INSERT INTO "ConsentEvents" ("ConsentId","FromState","ToState","At","ActorKind","Reason") VALUES (@c, 'SENT', 'SIGNED', @at, 'signer', 'patient');
            """, signer, tx))
        {
            command.Parameters.AddWithValue("c", consentId);
            command.Parameters.AddWithValue("at", SignedAt.UtcDateTime);
            await command.ExecuteNonQueryAsync(Ct);
        }

        // Act
        var archive = Task.Run(async () =>
        {
            await using var db = s.CreateDbContext();
            return await Anamnys.Server.Patients.PatientRecords.SetArchivedAsync(db, s.Provider, s.Adult, true, Later, Ct);
        }, Ct);
        await Task.Delay(500, Ct);
        var waited = !archive.IsCompleted;
        await tx.CommitAsync(Ct);
        var archived = await archive;

        // Assert
        archived.Should().BeTrue();
        waited.Should().BeTrue("the archive waits for the signature's row lock");
        (await s.EventsAsync(consentId, Ct)).Should().Equal(
            "NOT_REQUESTED>SENT provider -", "SENT>SIGNED signer patient", "SIGNED>EXPIRED provider discharge");
        (await s.EffectiveAsync(s.Adult, Ct, Later))!.ExpiredReason.Should().Be("discharge");
    }

    [Fact]
    public async Task Sign_AfterTheProviderRenamed_SignsTheTextThatWasShown()
    {
        // Arrange
        await using var s = await ConsentScene.CreateAsync(fixture, Ct);
        await s.SendAsync(s.Adult, Ct);
        var token = s.SignToken();
        await using var db = s.CreateDbContext();
        var shown = (await RecordingConsentService.ViewSignAsync(db, token, Now, Ct))!.Text;
        await s.Db.ExecuteAsync("""UPDATE "Providers" SET "Name" = 'Dra. Renomeada' WHERE "Id" = @p""", Ct, ("p", s.Provider));

        // Act
        var signed = await s.SignAsync(token, AdultSignature(), Ct);
        var consent = await db.RecordingConsents.AsNoTracking().SingleAsync(Ct);

        // Assert
        signed.Outcome.Should().Be(PublicConsentOutcome.Ok);
        shown.Should().Contain("Dra. Teste").And.NotContain("04/05/1990");
        consent.TermText.Should().Contain("Profissional: Dra. Teste").And.NotContain("Renomeada").And.Contain("04/05/1990");
        ConsentTerm.MaskBirthDate(consent.TermText!).Should().StartWith(shown.Split(ConsentTerm.SignaturePending)[0]);
        consent.TermSha256.Should().Be(ConsentTerm.Sha256(consent.TermText!));
    }
}
