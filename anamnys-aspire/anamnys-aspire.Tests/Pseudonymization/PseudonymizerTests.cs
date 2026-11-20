using Anamnys.Server.Pseudonymization;
using Anamnys.Tests.Retention;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Pseudonymization;

// Tokens and exclusions (design/specs/2026-11-17-pseudonymization-design.md §3, §4, §8) on a scratch database from the
// bootstrap script (which carries the F-27 migration). The detector is a fake with fixed answers.
[Collection(SharedAppHostCollection.Name)]
public class PseudonymizerTests(SharedAppHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string P = EntityKinds.Person;

    [Fact]
    public async Task PseudonymizeAsync_SameNameInSessions3And12_ResolvesToTheSameToken()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(FakeThirdPartyDetector.Finding(("Joana", P), ("Carlos", P), ("Rita", P)));
        await using var db = s.CreateDbContext();
        var results = new List<string>();

        // Act: session 3 names Joana, sessions 4–11 name others, session 12 names Joana again.
        for (var session = 3; session <= 12; session++)
        {
            var text = session switch
            {
                3 => "Falou da Joana hoje.",
                12 => "Carlos e Joana brigaram.",
                _ => "Encontrou a Rita.",
            };
            results.Add((await pseudonymizer.PseudonymizeAsync(db, provider, patient, text, Ct)).Text);
        }

        // Assert
        results[0].Should().Be("Falou da [P1] hoje.");
        results[1].Should().Be("Encontrou a [P2].");
        results[^1].Should().Be("[P3] e [P1] brigaram.");
    }

    [Fact]
    public async Task PseudonymizeAsync_AccentAndCaseVariants_GetTheSameToken()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(FakeThirdPartyDetector.Finding(("José Pereira", P), ("JOSE PEREIRA", P), ("jose  pereira", P)));
        await using var db = s.CreateDbContext();

        // Act
        var first = await pseudonymizer.PseudonymizeAsync(db, provider, patient, "Encontrou José Pereira.", Ct);
        var second = await pseudonymizer.PseudonymizeAsync(db, provider, patient, "Gritou JOSE PEREIRA e jose  pereira respondeu.", Ct);

        // Assert
        first.Text.Should().Be("Encontrou [P1].");
        second.Text.Should().Be("Gritou [P1] e [P1] respondeu.");
        second.CountsByKind[P].Should().Be(2);
    }

    [Fact]
    public async Task PseudonymizeAsync_TwoPatients_NumberIndependently()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var ana = await s.AddPatientAsync(provider, "Ana", Ct);
        var bia = await s.AddPatientAsync(provider, "Bia", Ct);
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(FakeThirdPartyDetector.Finding(("Joana", P), ("Pedro", P)));
        await using var db = s.CreateDbContext();

        // Act
        var first = await pseudonymizer.PseudonymizeAsync(db, provider, ana, "Joana e Pedro vieram.", Ct);
        var second = await pseudonymizer.PseudonymizeAsync(db, provider, bia, "Pedro veio.", Ct);

        // Assert
        first.Text.Should().Be("[P1] e [P2] vieram.");
        second.Text.Should().Be("[P1] veio.");
        (await s.CountAsync("""SELECT count(*) FROM "PatientPseudonymSecrets" WHERE "PatientId" = ANY(@p)""", Ct, ("p", new[] { ana, bia }))).Should().Be(2);
    }

    [Fact]
    public async Task PseudonymizeAsync_KindsNumberedSeparately_AddressesUnnumbered_AndNoNameIsStored()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(FakeThirdPartyDetector.Finding(
            ("Joana", P), ("Pedro", P), ("Padaria Central", EntityKinds.Organization), ("Loja Sol", EntityKinds.Organization),
            ("Colégio Objetivo", EntityKinds.School), ("Rua Azul, 10", EntityKinds.Address), ("Avenida Brasil", EntityKinds.Address)));
        await using var db = s.CreateDbContext();
        const string text = "Joana trabalha na Padaria Central, estuda no Colégio Objetivo e mora na Rua Azul, 10. "
            + "Pedro trabalha na Padaria Central e na Loja Sol, na Avenida Brasil.";

        // Act
        var result = await pseudonymizer.PseudonymizeAsync(db, provider, patient, text, Ct);

        // Assert
        result.Text.Should().Be("[P1] trabalha na [Empresa 1], estuda no [Escola 1] e mora na [Endereço]. "
            + "[P2] trabalha na [Empresa 1] e na [Empresa 2], na [Endereço].");
        result.CountsByKind.Should().Equal(new Dictionary<string, int> { [P] = 2, ["organization"] = 3, ["school"] = 1, ["address"] = 2 });
        result.AuditValue.Should().Be("person=2,organization=3,school=1,address=2");
        var tokens = await db.PseudonymTokens.AsNoTracking().Where(t => t.PatientId == patient).OrderBy(t => t.Kind).ThenBy(t => t.Number)
            .Select(t => new { t.Kind, t.Number }).ToListAsync(Ct);
        tokens.Select(t => (t.Kind, t.Number)).Should().Equal(("organization", 1), ("organization", 2), (P, 1), (P, 2), ("school", 1));
        // Whole names: single words such as "Objetivo" also occur in the seeded vocabulary and templates.
        foreach (var name in new[] { "Joana", "Pedro", "Padaria Central", "Colégio Objetivo", "Rua Azul", "Loja Sol" })
        {
            (await PseudonymizationScratch.NameAppearsAnywhereAsync(s, name)).Should().BeFalse($"{name} must not be stored");
        }
    }

    [Fact]
    public async Task PseudonymizeAsync_PatientAndProviderNamesAndKinshipWords_AreNeverReplaced()
    {
        // Arrange: the scratch provider is "Dra. Teste", the patient "Ana Silva".
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var detector = FakeThirdPartyDetector.Finding(("Ana Silva", P), ("Ana", P), ("Teste", P), ("Dra. Teste", P), ("mãe", P),
            ("tia Maria", P), ("ele", P));
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(detector);
        await using var db = s.CreateDbContext();

        // Act
        var result = await pseudonymizer.PseudonymizeAsync(db, provider, patient,
            "Ana Silva contou que a mãe e a tia Maria discutiram. Ana disse à Dra. Teste que ele saiu e Eu fiquei.", Ct);

        // Assert
        result.Text.Should().Be("Ana Silva contou que a mãe e a tia [P1] discutiram. Ana disse à Dra. Teste que ele saiu e Eu fiquei.");
        detector.LastExclusions.Should().Contain(["Ana", "Silva", "Ana Silva", "Teste"]);
    }

    [Fact]
    public async Task PseudonymizeAsync_FirstNameMissedByTheDetector_IsReplacedByTheSafetyNet()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(FakeThirdPartyDetector.Finding(("Bruna", P)));
        await using var db = s.CreateDbContext();

        // Act
        var result = await pseudonymizer.PseudonymizeAsync(db, provider, patient,
            "Mariana ligou para a Bruna. Depois Mariana Costa voltou.", Ct);

        // Assert: "Mariana" alone and "Mariana Costa" are different names, so different tokens.
        result.Text.Should().Be("[P1] ligou para a [P2]. Depois [P3] voltou.");
    }

    [Fact]
    public async Task PseudonymizeAsync_ConcurrentRunsForOnePatient_ShareOneSecretAndNumbering()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var protection = new EphemeralDataProtectionProvider();
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(FakeThirdPartyDetector.Finding(("Joana", P), ("Pedro", P)), protection: protection);
        await using var first = s.CreateDbContext();
        await using var second = s.CreateDbContext();

        // Act
        var results = await Task.WhenAll(
            pseudonymizer.PseudonymizeAsync(first, provider, patient, "Joana veio.", Ct),
            pseudonymizer.PseudonymizeAsync(second, provider, patient, "Pedro veio.", Ct));
        await using var third = s.CreateDbContext();
        var again = await pseudonymizer.PseudonymizeAsync(third, provider, patient, "Pedro e Joana.", Ct);

        // Assert
        results.Select(r => r.Text).Should().BeEquivalentTo(["[P1] veio.", "[P2] veio."]);
        var pedro = results[1].Text[..4];
        again.Text.Should().Be($"{pedro} e {(pedro == "[P1]" ? "[P2]" : "[P1]")}.");
        (await s.CountAsync("""SELECT count(*) FROM "PatientPseudonymSecrets" WHERE "PatientId" = @p""", Ct, ("p", patient))).Should().Be(1);
    }

    [Fact]
    public async Task PseudonymizeAsync_DetectorReturnsTheFirstNameOnly_TheSurnameAndHyphenatedPartAreNotLeftBare()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        // "Kelvia" is not in the first-name list: only the extension of the detected span can cover "Kelvia Pereira".
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(FakeThirdPartyDetector.Finding(("Joana", P), ("Kelvia", P)));
        await using var db = s.CreateDbContext();

        // Act
        var result = await pseudonymizer.PseudonymizeAsync(db, provider, patient,
            "A Joana Pereira chegou com Kelvia Souto. Depois Joana-Maria saiu.", Ct);

        // Assert
        result.Text.Should().Be("A [P1] chegou com [P2]. Depois [P3] saiu.");
        result.Text.Should().NotContain("Pereira").And.NotContain("Souto").And.NotContain("Maria");
    }

    [Fact]
    public async Task PseudonymizeAsync_NameFollowedByAPronounOrThePatientsName_KeepsTheSameTokenAndLeavesThePatientAlone()
    {
        // Arrange: the patient is "Ana Silva"; an unpunctuated transcript runs "Joana Ela…" and "Joana Ana…".
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(FakeThirdPartyDetector.Finding(("Joana", P)));
        await using var db = s.CreateDbContext();

        // Act
        var result = await pseudonymizer.PseudonymizeAsync(db, provider, patient,
            "Falou com a Joana Ela disse que a Joana Ana chorou e a Joana Pereira saiu.", Ct);

        // Assert
        result.Text.Should().Be("Falou com a [P1] Ela disse que a [P1] Ana chorou e a [P2] saiu.");
    }

    [Fact]
    public async Task PseudonymizeAsync_SchoolNameCutBetweenTwoChunks_IsReplacedWhole()
    {
        // Arrange: no sentence end, so the 100-character chunk is cut at the space inside "Colégio Objetivo" (index 97).
        // The fake finds the school only when a chunk holds the whole name.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var text = string.Concat(Enumerable.Repeat("fala ", 18)) + "Colégio Objetivo " + string.Concat(Enumerable.Repeat("fala ", 30)).TrimEnd();
        var detector = new FakeThirdPartyDetector(chunk => chunk.Contains("Colégio Objetivo") ? [("Colégio Objetivo", EntityKinds.School)] : []);
        var withOverlap = PseudonymizationKit.Pseudonymizer(detector, chunkChars: 100, overlapChars: 30);
        var withoutOverlap = PseudonymizationKit.Pseudonymizer(detector, chunkChars: 100, overlapChars: 0);
        await using var db = s.CreateDbContext();

        // Act
        var cut = Pseudonymizer.Segments(text, 100)[0];
        var missed = await withoutOverlap.PseudonymizeAsync(db, provider, patient, text, Ct);
        var result = await withOverlap.PseudonymizeAsync(db, provider, patient, text, Ct);

        // Assert
        text.Substring(cut.Start, cut.Length).Should().EndWith("Colégio");
        missed.Text.Should().Contain("Objetivo");
        result.Text.Should().Contain("[Escola 1]").And.NotContain("Colégio").And.NotContain("Objetivo");
        result.CountsByKind[EntityKinds.School].Should().Be(1);
    }

    [Fact]
    public async Task PseudonymizeAsync_OverlappingDetectorSpansOfDifferentKinds_KeepTheLongest()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(FakeThirdPartyDetector.Finding(
            ("Escola Estadual Paulo Freire", EntityKinds.School), ("Paulo Freire", P), ("Clínica Bem Estar", EntityKinds.Organization), ("Bem Estar", EntityKinds.School)));
        await using var db = s.CreateDbContext();

        // Act
        var result = await pseudonymizer.PseudonymizeAsync(db, provider, patient,
            "Estuda na Escola Estadual Paulo Freire e faz terapia na Clínica Bem Estar.", Ct);

        // Assert
        result.Text.Should().Be("Estuda na [Escola 1] e faz terapia na [Empresa 1].");
        result.CountsByKind[P].Should().Be(0);
    }

    [Fact]
    public async Task PseudonymizeAsync_DecomposedAccents_ArticlesAndTitles_GetTheSameTokenAsThePlainName()
    {
        // Arrange: "José" composed (NFC) and decomposed (NFD), "o José", "do José", "Dr. José".
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var nfd = "José".Normalize(System.Text.NormalizationForm.FormD);
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(FakeThirdPartyDetector.Finding(("José", P), (nfd, P), ("o Pedro", P), ("do Pedro", P), ("pro Pedro", P)));
        await using var db = s.CreateDbContext();

        // Act
        var first = await pseudonymizer.PseudonymizeAsync(db, provider, patient, "Encontrou José e Pedro.", Ct);
        var second = await pseudonymizer.PseudonymizeAsync(db, provider, patient, $"Falou com {nfd}, o Pedro ouviu e mandou pro Pedro a carta do Pedro.", Ct);

        // Assert
        first.Text.Should().Be("Encontrou [P1] e [P2].");
        second.Text.Should().Be("Falou com [P1], o [P2] ouviu e mandou pro [P2] a carta do [P2].");
    }

    [Fact]
    public async Task PseudonymizeAsync_ThirdPartyWithTheProvidersFirstName_IsReplaced_ButTheProvidersFullNameIsNot()
    {
        // Arrange: the provider is "Dra. Beatriz Ramos".
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var provider = await s.AddProviderAsync(Ct);
        await s.ExecuteAsync("""UPDATE "Providers" SET "Name" = 'Dra. Beatriz Ramos' WHERE "Id" = @p""", Ct, ("p", provider));
        var patient = await s.AddPatientAsync(provider, "Ana", Ct);
        var pseudonymizer = PseudonymizationKit.Pseudonymizer(FakeThirdPartyDetector.Finding(("Beatriz", P), ("Beatriz Ramos", P)));
        await using var db = s.CreateDbContext();

        // Act
        var result = await pseudonymizer.PseudonymizeAsync(db, provider, patient,
            "Contou à Dra. Beatriz Ramos que a Beatriz do trabalho a criticou.", Ct);

        // Assert
        result.Text.Should().Be("Contou à Dra. Beatriz Ramos que a [P1] do trabalho a criticou.");
    }

    [Fact]
    public async Task Secret_WrittenThroughThePostgresKeyRing_IsReadAfterARestart_AndDiscardStillWorks()
    {
        // Arrange: two key-ring instances over the same database stand for the process before and after a restart.
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new Anamnys.Tests.Notes.FakeNoteFileStore();
        var r = await DiscardServiceTests.SeedExpiredAsync(s, store);
        var other = await s.AddPatientAsync(r.Provider, "Bia", Ct);
        // The discard seed carries a placeholder secret and token (not protected by any ring): this test makes its own.
        await s.ExecuteAsync("""DELETE FROM "PseudonymTokens" WHERE "PatientId" = @p""", Ct, ("p", r.Patient));
        await s.ExecuteAsync("""DELETE FROM "PatientPseudonymSecrets" WHERE "PatientId" = @p""", Ct, ("p", r.Patient));
        string connection;
        await using (var db = s.CreateDbContext())
        {
            connection = db.Database.GetConnectionString()!;
        }
        var detector = FakeThirdPartyDetector.Finding(("Joana", P), ("Pedro", P));
        IDataProtectionProvider Ring() => PseudonymizationKeyRing.Create(new PostgresXmlRepository(connection), null);
        var before = PseudonymizationKit.Pseudonymizer(detector, protection: Ring());

        // Act
        string first, afterRestart, otherPatient;
        await using (var db = s.CreateDbContext())
        {
            first = (await before.PseudonymizeAsync(db, r.Provider, r.Patient, "Joana e Pedro vieram.", Ct)).Text;
        }
        var restarted = PseudonymizationKit.Pseudonymizer(detector, protection: Ring());
        await using (var db = s.CreateDbContext())
        {
            afterRestart = (await restarted.PseudonymizeAsync(db, r.Provider, r.Patient, "Pedro voltou sem a Joana.", Ct)).Text;
        }
        var keys = await s.CountAsync("""SELECT count(*) FROM "PseudonymizationKeys" """, Ct);
        await using (var db = s.CreateDbContext())
        {
            (await Anamnys.Server.Retention.DiscardService.DiscardAsync(db, store, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                r.Provider, r.Patient, "Ana Silva", new DateTimeOffset(2026, 10, 27, 15, 0, 0, TimeSpan.Zero), Ct)).Outcome
                .Should().Be(Anamnys.Server.Retention.DiscardOutcome.Ok);
        }
        await using (var db = s.CreateDbContext())
        {
            otherPatient = (await restarted.PseudonymizeAsync(db, r.Provider, other, "Joana veio.", Ct)).Text;
        }

        // Assert
        first.Should().Be("[P1] e [P2] vieram.");
        afterRestart.Should().Be("[P2] voltou sem a [P1].");
        keys.Should().BeGreaterThan(0);
        (await s.CountAsync("""SELECT count(*) FROM "PseudonymTokens" WHERE "PatientId" = @p""", Ct, ("p", r.Patient))).Should().Be(0);
        (await s.CountAsync("""SELECT count(*) FROM "PatientPseudonymSecrets" WHERE "PatientId" = @p""", Ct, ("p", r.Patient))).Should().Be(0);
        (await s.CountAsync("""SELECT count(*) FROM "PseudonymizationKeys" """, Ct)).Should().Be(keys);
        otherPatient.Should().Be("[P1] veio.");
    }

    [Fact]
    public async Task Discard_ForgetsTheHeldTextOfThePatientsNotes()
    {
        // Arrange
        await using var s = await RetentionScratch.CreateAsync(fixture, Ct);
        var store = new Anamnys.Tests.Notes.FakeNoteFileStore();
        var r = await DiscardServiceTests.SeedExpiredAsync(s, store);
        var holds = new PseudonymizationHolds(TimeProvider.System);
        holds.Hold(r.Notes[1], r.Provider, r.Patient, "Joana ligou.");
        var chars = holds.HeldChars(r.Notes[1])!;
        await using var db = s.CreateDbContext();

        // Act
        var result = await Anamnys.Server.Retention.DiscardService.DiscardAsync(db, store, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            r.Provider, r.Patient, "Ana Silva", new DateTimeOffset(2026, 10, 27, 15, 0, 0, TimeSpan.Zero), Ct, holds);

        // Assert
        result.Outcome.Should().Be(Anamnys.Server.Retention.DiscardOutcome.Ok);
        holds.HeldChars(r.Notes[1]).Should().BeNull();
        chars.Should().OnlyContain(c => c == '\0');
    }
}
