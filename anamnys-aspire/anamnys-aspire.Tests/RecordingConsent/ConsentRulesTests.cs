using Anamnys.Server.Data.Entities;
using Anamnys.Server.RecordingConsents;
using FluentAssertions;

namespace Anamnys.Tests.RecordingConsents;

// The capture rule, the due expiries and the term text (design/specs/2026-11-15-recording-consent-design.md §3, §4).
public class ConsentRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 15, 0, 0, TimeSpan.Zero);

    private static Patient Adult(DateTimeOffset? archivedAt = null) =>
        new() { Id = Guid.NewGuid(), FirstName = "Ana", LastName = "Silva", DateOfBirth = new DateOnly(1990, 5, 4), ArchivedAt = archivedAt };

    private static Patient Minor() =>
        new() { Id = Guid.NewGuid(), FirstName = "Caio", LastName = "Souza", DateOfBirth = new DateOnly(2012, 3, 10) };

    private static RecordingConsent Consent(string state, string? role = null, DateTimeOffset? linkExpires = null, DateTimeOffset? assent = null) =>
        new()
        {
            Id = Guid.NewGuid(), State = state, SignerRole = role, TemplateVersion = ConsentTerm.CurrentVersion,
            SignTokenExpiresAt = linkExpires ?? Now.AddDays(3), AssentRecordedAt = assent,
        };

    [Theory]
    [InlineData(ConsentRules.NotRequested, "none")]
    [InlineData(ConsentRules.Sent, "sent")]
    [InlineData(ConsentRules.Revoked, "revoked")]
    [InlineData(ConsentRules.Expired, "expired")]
    [InlineData(ConsentRules.Signed, "ok")]
    public void CanRecordSession_EveryState_AllowsOnlySigned(string state, string reason)
    {
        // Arrange
        var consent = Consent(state, ConsentRules.RolePatient);

        // Act
        var (allowed, why) = ConsentRules.CanRecordSession(consent, Adult(), Now);

        // Assert
        allowed.Should().Be(state == ConsentRules.Signed);
        why.Should().Be(reason);
    }

    [Fact]
    public void CanRecordSession_NoConsentArchivedOrMinorWithoutAssent_IsRefusedWithItsReason()
    {
        // Arrange
        var signed = Consent(ConsentRules.Signed, ConsentRules.RoleGuardian);
        var withAssent = Consent(ConsentRules.Signed, ConsentRules.RoleGuardian, assent: Now.AddDays(-1));

        // Act
        var none = ConsentRules.CanRecordSession(null, Adult(), Now);
        var archived = ConsentRules.CanRecordSession(Consent(ConsentRules.Signed, ConsentRules.RolePatient), Adult(Now.AddDays(-1)), Now);
        var missingAssent = ConsentRules.CanRecordSession(signed, Minor(), Now);
        var minorOk = ConsentRules.CanRecordSession(withAssent, Minor(), Now);

        // Assert
        none.Should().Be((false, "none"));
        archived.Should().Be((false, "archived"));
        missingAssent.Should().Be((false, "assent_missing"));
        minorOk.Should().Be((true, "ok"));
    }

    [Fact]
    public void DueExpiry_LinkDischargeAndAdulthood_AreFoundAndAStaleSignedNeverAllowsCapture()
    {
        // Arrange
        var sentPast = Consent(ConsentRules.Sent, linkExpires: Now.AddMinutes(-1));
        var guardianSigned = Consent(ConsentRules.Signed, ConsentRules.RoleGuardian, assent: Now.AddYears(-4));
        var grownUp = Minor();
        var eighteenth = new DateTimeOffset(2030, 3, 10, 15, 0, 0, TimeSpan.Zero);

        // Act
        var link = ConsentRules.DueExpiry(sentPast, Adult(), Now);
        var discharge = ConsentRules.DueExpiry(Consent(ConsentRules.Signed, ConsentRules.RolePatient), Adult(Now), Now);
        var adulthood = ConsentRules.DueExpiry(guardianSigned, grownUp, eighteenth);
        var dayBefore = ConsentRules.DueExpiry(guardianSigned, grownUp, eighteenth.AddDays(-1));
        var capture = ConsentRules.CanRecordSession(guardianSigned, grownUp, eighteenth);

        // Assert
        link.Should().Be("link");
        discharge.Should().Be("discharge");
        adulthood.Should().Be("adulthood");
        dayBefore.Should().BeNull();
        capture.Should().Be((false, "expired"));
        ConsentRules.IsMinor(grownUp, eighteenth).Should().BeFalse();
        ConsentRules.IsMinor(grownUp, eighteenth.AddDays(-1)).Should().BeTrue();
    }

    [Fact]
    public void Transitions_OnlyAllowedStates_AcceptEachAction()
    {
        // Arrange
        var sent = Consent(ConsentRules.Sent);
        var signed = Consent(ConsentRules.Signed);
        var revoked = Consent(ConsentRules.Revoked);
        var linkExpired = Consent(ConsentRules.Expired);
        linkExpired.ExpiredReason = ConsentRules.ExpiredLink;
        var discharged = Consent(ConsentRules.Expired);
        discharged.ExpiredReason = ConsentRules.ExpiredDischarge;
        var oldVersion = Consent(ConsentRules.Signed);
        oldVersion.TemplateVersion = "v0";

        // Act / Assert
        ConsentRules.CanResend(sent).Should().BeTrue();
        ConsentRules.CanResend(linkExpired).Should().BeTrue();
        ConsentRules.CanResend(discharged).Should().BeFalse();
        ConsentRules.CanResend(signed).Should().BeFalse();
        ConsentRules.CanRevokeByProvider(signed).Should().BeTrue();
        ConsentRules.CanRevokeByProvider(revoked).Should().BeFalse();
        ConsentRules.CanRecordAssent(revoked).Should().BeFalse();
        ConsentRules.CanRenew(signed).Should().BeFalse();
        ConsentRules.CanRenew(oldVersion).Should().BeTrue();
    }

    [Fact]
    public void Render_V1_NamesEveryPartyAndClause_HidesTheBirthDateWhenAsked_AndAddsTheNote()
    {
        // Arrange
        var data = new ConsentTermData("Caio Souza", new DateOnly(2012, 3, 10), true, "Dra. Teste", "123456", "06", "São Paulo",
            "Sessões às terças.");
        var signature = new ConsentSignature("Joana Souza", ConsentRules.RoleGuardian, "mãe", Now);

        // Act
        var open = ConsentTerm.Render("v1", data);
        var hidden = ConsentTerm.Render("v1", data with { ShowBirthDate = false });
        var signed = ConsentTerm.Render("v1", data with { Signature = signature });
        var unknown = () => ConsentTerm.Render("v9", data);

        // Assert
        open.Should().Contain("Caio Souza").And.Contain("10/03/2012").And.Contain("Dra. Teste").And.Contain("CRP 123456/06")
            .And.Contain("Responsável legal: nome e grau de parentesco");
        foreach (var heading in new[] { "1. Partes", "2. Finalidade", "3. O que é gravado", "4. Processamento", "5. Destruição do áudio",
                     "6. O que é guardado", "7. Acesso", "8. Participação voluntária", "9. Revogação", "10. Paciente menor de idade",
                     "11. Data e local" })
        {
            open.Should().Contain(heading);
        }
        open.Should().Contain("Observações do profissional\nSessões às terças.");
        hidden.Should().NotContain("10/03/2012");
        signed.Should().Contain("Responsável legal: Joana Souza (mãe).").And.Contain("São Paulo, 15/11/2026 às 12:00")
            .And.Contain("Assinado eletronicamente por Joana Souza, responsável legal (mãe).");
        ConsentTerm.MaskBirthDate(signed).Should().NotContain("10/03/2012");
        unknown.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Render_V1_PrintsItsOwnVersionLiterally()
    {
        // Arrange
        var data = new ConsentTermData("Ana Silva", new DateOnly(1990, 5, 4), false, "Dra. Teste", "123456", "06", null, null);

        // Act
        var text = ConsentTerm.Render("v1", data);

        // Assert: the template's own literal, not CurrentVersion (which will move on to v2 one day).
        text.Split('\n')[1].Should().Be("Versão do termo: v1");
    }

    [Fact]
    public void Sha256_KnownVector_IsLowerHex()
    {
        // Act
        var hash = ConsentTerm.Sha256("abc");

        // Assert
        hash.Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void Complete_FillsOnlyTheOpenLines_AndRefusesASentTextThatDoesNotFitTheSigner()
    {
        // Arrange
        var minor = new ConsentTermData("Caio Souza", new DateOnly(2012, 3, 10), true, "Dra. Teste", "123456", "06", "São Paulo", "Nota.");
        var adult = minor with { PatientName = "Ana Silva", Minor = false };
        var guardian = new ConsentSignature("Joana Souza", ConsentRules.RoleGuardian, "mãe", Now);
        var self = new ConsentSignature("Ana Silva", ConsentRules.RolePatient, null, Now);
        var sentMinor = ConsentTerm.Render("v1", minor);
        var sentAdult = ConsentTerm.Render("v1", adult);

        // Act
        var completedMinor = ConsentTerm.Complete(sentMinor, guardian, "São Paulo");
        var completedAdult = ConsentTerm.Complete(sentAdult, self, "São Paulo");
        var adultOnMinorText = ConsentTerm.Complete(sentMinor, self, "São Paulo");
        var guardianOnAdultText = ConsentTerm.Complete(sentAdult, guardian, "São Paulo");
        var alreadySigned = ConsentTerm.Complete(completedAdult!, self, "São Paulo");

        // Assert
        completedMinor.Should().Be(ConsentTerm.Render("v1", minor with { Signature = guardian }));
        completedAdult.Should().Be(ConsentTerm.Render("v1", adult with { Signature = self }));
        adultOnMinorText.Should().BeNull();
        guardianOnAdultText.Should().BeNull();
        alreadySigned.Should().BeNull();
    }

    [Fact]
    public void RequestEmail_ForAMinor_AddressesTheGuardianWithoutThePatientsDetails()
    {
        // Act
        var minor = ConsentEmails.Request("familia@example.test", "Caio", "Dra. Teste", true, new Uri("https://portal.test/consentimento/t"), Now.AddDays(7));
        var adult = ConsentEmails.Request("ana@example.test", "Ana", "Dra. Teste", false, new Uri("https://portal.test/consentimento/t"), Now.AddDays(7));

        // Assert
        minor.Text.Should().StartWith("Olá.\n").And.Contain("Dra. Teste pede o consentimento do(a) responsável legal por Caio")
            .And.Contain("O link vale até 22/11/2026.");
        adult.Text.Should().StartWith("Olá, Ana.\n").And.Contain("pede o seu consentimento");
        minor.Html.Should().Contain("href=\"https://portal.test/consentimento/t\"");
    }
}
