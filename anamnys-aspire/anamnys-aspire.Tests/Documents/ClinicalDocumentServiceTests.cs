using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using Anamnys.Tests.Patients;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Documents;

[Collection(SharedAppHostCollection.Name)]
public class ClinicalDocumentServiceTests(SharedAppHostFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 14, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string> CompleteAtestado = new()
    {
        ["nome_solicitante"] = "Escola X",
        ["finalidade"] = "Afastamento",
        ["descricao_condicoes_psicologicas"] = "Linha um.\nLinha dois.",
        ["prazo_validade"] = "30 dias.",
    };

    private static readonly string[] AtestadoConfirmations = ["informacao_alem_do_solicitado"];

    private async Task<(PatientSearchSeed Seed, Guid PatientId)> SeedAsync(
        bool withCrp = true, bool withCity = true, string mode = "blocking")
    {
        var seed = await PatientSearchSeed.CreateAsync(fixture, Ct);
        await seed.ExecuteSqlAsync(
            """
            UPDATE "Providers" SET "CrpNumber" = CASE WHEN @crp THEN '12345' END, "CrpRegion" = CASE WHEN @crp THEN '06' END,
                   "PracticeCity" = CASE WHEN @city THEN 'São Paulo' END, "DocumentCheckMode" = @mode
            WHERE "Id" = @id
            """,
            Ct, ("crp", withCrp), ("city", withCity), ("mode", mode), ("id", seed.ProviderId));
        var patientId = await seed.AddPatientAsync("Ana", "Souza", cancellationToken: Ct);
        return (seed, patientId);
    }

    private static async Task<Guid> CreateDraftAsync(
        PatientSearchSeed seed, Guid patientId, string kind, Dictionary<string, string>? fields = null, string[]? confirmations = null,
        TeamMember[]? team = null)
    {
        await using var db = seed.CreateDbContext();
        var created = await ClinicalDocumentService.CreateAsync(db, seed.ProviderId, patientId, kind, null, Now, Ct);
        var id = created.Id!.Value;
        if (fields is not null)
        {
            await ClinicalDocumentService.SaveAsync(db, seed.ProviderId, id, fields, confirmations ?? [], team ?? [], Now, Ct);
        }
        return id;
    }

    private static readonly Dictionary<string, string> CompleteLaudo = new()
    {
        ["nome_solicitante"] = "Juízo", ["finalidade"] = "Avaliar", ["descricao_demanda"] = "Queixa",
        ["procedimento"] = "Entrevistas", ["referencial_teorico_metodologico"] = "TCC",
        ["pessoas_ouvidas_numero_de_encontros_e_duracao"] = "1 pessoa", ["analise"] = "Análise", ["conclusao"] = "Fim",
        ["referencias"] = "Fonte A", ["prazo_validade"] = "60 dias",
    };

    private static readonly string[] LaudoConfirmations = ["descricao_literal_das_sessoes", "afirmacao_sem_fonte_ou_sustentacao"];

    private static readonly Dictionary<string, string> CompleteParecer = new()
    {
        ["nome_solicitante"] = "Juízo", ["finalidade"] = "Esclarecer", ["nome_autor_inscricao_crp_e_titulacao"] = "Dra. Paula, CRP 06/12345, mestre",
        ["descricao_demanda"] = "Questão", ["analise"] = "Análise", ["conclusao"] = "Fim", ["referencias"] = "Fonte A",
        ["prazo_validade"] = "60 dias",
    };

    private static readonly string[] ParecerConfirmations = ["resultado_de_avaliacao_ou_intervencao_psicologica"];

    private static readonly Dictionary<string, string> CompleteMulti = new()
    {
        ["nome_solicitante"] = "Escola", ["finalidade"] = "Avaliar", ["descricao_demanda"] = "Queixa",
        ["procedimento"] = "Entrevistas", ["procedimentos_privativos_da_psicologia_em_secao_separada"] = "Testes",
        ["analise"] = "Análise psi", ["conclusao"] = "Fim", ["prazo_validade"] = "60 dias",
    };

    private static readonly string[] MultiConfirmations = ["afirmacao_sem_fonte_ou_sustentacao", "descricao_literal_das_sessoes"];

    private static readonly TeamMember[] RitaTeam = [new("Rita", "Psiquiatria", "CRM 1", "Análise da Rita")];

    [Theory]
    [InlineData("declaracao")]
    [InlineData("atestado")]
    [InlineData("relatorio")]
    [InlineData("laudo")]
    [InlineData("parecer")]
    [InlineData("relatorio_multiprofissional")]
    public async Task Create_AvailableKind_CreatesDraftWithLatestRulesetAndAudit(string kind)
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.CreateAsync(db, seed.ProviderId, patientId, kind, null, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        var doc = await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == result.Id, Ct);
        doc.Kind.Should().Be(kind);
        doc.SignedAt.Should().BeNull();
        doc.RulesetId.Should().NotBeNull();
        var ruleset = await db.ConformityRulesets.AsNoTracking().SingleAsync(r => r.Id == doc.RulesetId, Ct);
        ruleset.DocumentKind.Should().Be(kind);
        (await db.AuditEntries.AsNoTracking().Where(a => a.DocumentId == doc.Id).Select(a => a.Action).ToListAsync(Ct))
            .Should().Equal("document_created");
    }

    [Fact]
    public async Task Create_UnknownKind_ReturnsKindNotAvailable()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.CreateAsync(db, seed.ProviderId, patientId, "carta", null, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.KindNotAvailable);
    }

    [Fact]
    public async Task Create_ArchivedPatient_ReturnsArchived()
    {
        // Arrange
        var (seed, _) = await SeedAsync();
        await using var _ = seed;
        var archived = await seed.AddPatientAsync("Bia", "Lima", archivedAt: Now, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.CreateAsync(db, seed.ProviderId, archived, "declaracao", null, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Archived);
    }

    [Fact]
    public async Task Create_OtherProvidersPatient_ReturnsNotFound()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var otherProvider = await seed.AddProviderAsync(Ct);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.CreateAsync(db, otherProvider, patientId, "declaracao", null, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.NotFound);
    }

    [Fact]
    public async Task Create_NoteOfAnotherPatient_ReturnsNotFound()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var other = await seed.AddPatientAsync("Caio", "Reis", cancellationToken: Ct);
        await seed.AddNoteAsync(other, "Signed", Now, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var noteId = await db.Notes.AsNoTracking().Where(n => n.PatientId == other).Select(n => n.Id).SingleAsync(Ct);

        // Act
        var result = await ClinicalDocumentService.CreateAsync(db, seed.ProviderId, patientId, "declaracao", noteId, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.NotFound);
    }

    [Fact]
    public async Task Save_UnknownCodes_DropsThemAndKeepsProviderFieldsTrimmed()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "declaracao");
        await using var db = seed.CreateDbContext();
        var fields = new Dictionary<string, string>
        {
            ["finalidade"] = "  Comprovar  ",
            ["nome_pessoa_atendida"] = "Outro nome",
            ["inventado"] = "x",
        };

        // Act
        var result = await ClinicalDocumentService.SaveAsync(db, seed.ProviderId, id, fields, ["inventada", "sintomas_situacoes_ou_estados_psicologicos"], [], Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        var doc = await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        doc.Fields.Should().BeEquivalentTo(new Dictionary<string, string> { ["finalidade"] = "Comprovar" });
        doc.Confirmations.Should().Equal("sintomas_situacoes_ou_estados_psicologicos");
        var saved = await db.AuditEntries.AsNoTracking().Where(a => a.DocumentId == id && a.Action == "document_saved").ToListAsync(Ct);
        saved.Select(a => a.FieldChanged).Should().BeEquivalentTo("finalidade", "confirmations");
        var field = saved.Single(a => a.FieldChanged == "finalidade");
        field.PreviousValue.Should().Be("");
        field.NewValue.Should().Be("Comprovar");
        var confirmation = saved.Single(a => a.FieldChanged == "confirmations");
        confirmation.PreviousValue.Should().Be("");
        confirmation.NewValue.Should().Be("sintomas_situacoes_ou_estados_psicologicos");
    }

    [Fact]
    public async Task Save_SecondSaveChangingOneField_AuditsOnlyThatFieldWithPreviousValue()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "declaracao",
            new() { ["finalidade"] = "A", ["prazo_validade"] = "90 dias" }, ["sintomas_situacoes_ou_estados_psicologicos"]);
        await using var db = seed.CreateDbContext();
        var before = await db.AuditEntries.AsNoTracking().CountAsync(a => a.DocumentId == id, Ct);

        // Act
        await ClinicalDocumentService.SaveAsync(db, seed.ProviderId, id,
            new Dictionary<string, string> { ["finalidade"] = "B", ["prazo_validade"] = "90 dias" },
            ["sintomas_situacoes_ou_estados_psicologicos"], [], Now, Ct);

        // Assert
        var added = (await db.AuditEntries.AsNoTracking().Where(a => a.DocumentId == id).ToListAsync(Ct)).Count - before;
        added.Should().Be(1);
        var entry = await db.AuditEntries.AsNoTracking()
            .Where(a => a.DocumentId == id && a.FieldChanged == "finalidade").OrderByDescending(a => a.PreviousValue).FirstAsync(Ct);
        entry.PreviousValue.Should().Be("A");
        entry.NewValue.Should().Be("B");
    }

    [Fact]
    public async Task Save_NothingChanged_WritesNoAuditEntry()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var fields = new Dictionary<string, string> { ["finalidade"] = "A" };
        var id = await CreateDraftAsync(seed, patientId, "declaracao", fields, ["sintomas_situacoes_ou_estados_psicologicos"]);
        await using var db = seed.CreateDbContext();
        var before = await db.AuditEntries.AsNoTracking().CountAsync(a => a.DocumentId == id, Ct);

        // Act
        var result = await ClinicalDocumentService.SaveAsync(db, seed.ProviderId, id, fields, ["sintomas_situacoes_ou_estados_psicologicos"], [], Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.DocumentId == id, Ct)).Should().Be(before);
    }

    [Fact]
    public async Task Create_OwnNoteOfSamePatient_StoresNoteId()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        await seed.AddNoteAsync(patientId, "Signed", Now, cancellationToken: Ct);
        await using var db = seed.CreateDbContext();
        var noteId = await db.Notes.AsNoTracking().Where(n => n.PatientId == patientId).Select(n => n.Id).SingleAsync(Ct);

        // Act
        var result = await ClinicalDocumentService.CreateAsync(db, seed.ProviderId, patientId, "declaracao", noteId, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        (await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == result.Id, Ct)).NoteId.Should().Be(noteId);
    }

    [Fact]
    public async Task Sign_AdvisoryAcknowledgedWithoutConfirmations_ReturnsConfirmationsRequired()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync(mode: "advisory");
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "atestado", new() { ["finalidade"] = "x" });
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, true, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.ConfirmationsRequired);
    }

    [Fact]
    public async Task Correct_ArchivedPatient_ReturnsArchived()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "atestado", CompleteAtestado, AtestadoConfirmations);
        await using var db = seed.CreateDbContext();
        await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);
        await seed.ExecuteSqlAsync("""UPDATE "Patients" SET "ArchivedAt" = now() WHERE "Id" = @id""", Ct, ("id", patientId));

        // Act
        var result = await ClinicalDocumentService.CorrectAsync(db, seed.ProviderId, id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Archived);
        (await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct)).SupersededBy.Should().BeNull();
    }

    [Fact]
    public async Task Get_SignedDocumentAfterCrpRemoved_ReportsStoredCheckAndSigningCrp()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync(mode: "advisory");
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "atestado", new() { ["finalidade"] = "x" }, AtestadoConfirmations);
        await using var db = seed.CreateDbContext();
        await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, true, Now, Ct);
        await seed.ExecuteSqlAsync("""UPDATE "Providers" SET "CrpNumber" = NULL, "CrpRegion" = NULL WHERE "Id" = @id""", Ct, ("id", seed.ProviderId));

        // Act
        var detail = await ClinicalDocumentService.GetAsync(db, seed.ProviderId, id, Ct);

        // Assert
        detail!.Check.MissingElements.Should().BeEquivalentTo("nome_solicitante", "descricao_condicoes_psicologicas", "prazo_validade");
        detail.Mode.Should().Be("advisory");
        detail.ProviderHasCrp.Should().BeTrue("a signed document reports the CRP it was signed with");
        detail.SignedByCrp.Should().Be("12345/06");
    }

    [Fact]
    public async Task Save_FieldOverLimit_ReturnsInvalidWithCodeAndStoresNothing()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "declaracao");
        await using var db = seed.CreateDbContext();
        var fields = new Dictionary<string, string> { ["finalidade"] = new string('a', 20_001) };

        // Act
        var result = await ClinicalDocumentService.SaveAsync(db, seed.ProviderId, id, fields, [], [], Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Invalid);
        result.Missing.Should().Equal("finalidade");
        (await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct)).Fields.Should().BeEmpty();
    }

    [Fact]
    public async Task Save_AfterSign_ReturnsLocked()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "atestado", CompleteAtestado, AtestadoConfirmations);
        await using var db = seed.CreateDbContext();
        (await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct)).Outcome.Should().Be(DocumentOutcome.Ok);

        // Act
        var result = await ClinicalDocumentService.SaveAsync(db, seed.ProviderId, id, new Dictionary<string, string>(), [], [], Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Locked);
    }

    [Fact]
    public async Task Sign_BlockingModeWithMissingElements_ReturnsMissingElementsList()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "atestado", new() { ["finalidade"] = "x" }, AtestadoConfirmations);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, true, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.MissingElements);
        result.Missing.Should().BeEquivalentTo("nome_solicitante", "descricao_condicoes_psicologicas", "prazo_validade");
        (await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct)).SignedAt.Should().BeNull();
    }

    [Fact]
    public async Task Sign_AdvisoryModeAcknowledged_SignsAndRecordsFailedCheck()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync(mode: "advisory");
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "atestado", new() { ["finalidade"] = "x" }, AtestadoConfirmations);
        await using var db = seed.CreateDbContext();

        // Act
        var notAcknowledged = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, true, Now, Ct);

        // Assert
        notAcknowledged.Outcome.Should().Be(DocumentOutcome.MissingElements);
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        var check = await db.ConformityChecks.AsNoTracking().SingleAsync(c => c.DocumentId == id, Ct);
        check.Passed.Should().BeFalse();
        check.Mode.Should().Be("advisory");
        check.MissingElements.Should().BeEquivalentTo("nome_solicitante", "descricao_condicoes_psicologicas", "prazo_validade");
        check.ForbiddenFound.Should().BeEmpty();
    }

    [Fact]
    public async Task Sign_ConfirmationsMissing_ReturnsConfirmationsRequired()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "atestado", CompleteAtestado);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.ConfirmationsRequired);
        result.Missing.Should().Equal("informacao_alem_do_solicitado");
    }

    [Fact]
    public async Task Sign_NoCity_ReturnsMissingCity()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync(withCity: false);
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "atestado", CompleteAtestado, AtestadoConfirmations);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.MissingCity);
    }

    [Fact]
    public async Task Sign_NoCrp_ReturnsMissingCrp()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync(withCrp: false);
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "atestado", CompleteAtestado, AtestadoConfirmations);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.MissingCrp);
    }

    [Fact]
    public async Task Sign_CompleteAtestado_SignsWithCrpBodyCheckAndAudit()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "atestado", CompleteAtestado, AtestadoConfirmations);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        var doc = await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        doc.SignedAt.Should().Be(Now);
        doc.SignedByCrp.Should().Be("12345/06");
        doc.Body.Should().Be("Nome do solicitante: Escola X. Finalidade: Afastamento. "
            + "Descrição das condições psicológicas: Linha um. Linha dois. Prazo de validade: 30 dias.");
        doc.Body.Should().NotContain("\n");
        var check = await db.ConformityChecks.AsNoTracking().SingleAsync(c => c.DocumentId == id, Ct);
        check.Passed.Should().BeTrue();
        check.Mode.Should().Be("blocking");
        check.MissingElements.Should().BeEmpty();
        (await db.AuditEntries.AsNoTracking().Where(a => a.DocumentId == id).Select(a => a.Action).ToListAsync(Ct))
            .Distinct().Should().BeEquivalentTo("document_created", "document_saved", "document_signed");
    }

    [Fact]
    public async Task Sign_CompleteRelatorio_BodyHasLabelledBlocksWithValidityLast()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var fields = new Dictionary<string, string>
        {
            ["nome_solicitante"] = "Fulano", ["finalidade"] = "Avaliar", ["descricao_demanda"] = "Queixa",
            ["procedimento"] = "Entrevistas", ["referencial_teorico"] = "TCC",
            ["pessoas_ouvidas_numero_de_encontros_e_duracao"] = "1 pessoa", ["analise"] = "Análise", ["conclusao"] = "Fim",
            ["prazo_validade"] = "60 dias",
        };
        var id = await CreateDraftAsync(seed, patientId, "relatorio", fields,
            ["afirmacao_sem_fonte_ou_sustentacao", "descricao_literal_das_sessoes"]);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        var body = (await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct)).Body;
        body.Should().StartWith("Nome do solicitante\nFulano\n\nFinalidade\nAvaliar");
        body.Should().EndWith("Conclusão\nFim\n\nPrazo de validade\n60 dias");
    }

    [Fact]
    public async Task Sign_ThenProfileAndPatientChange_DetailAndPdfModelKeepSigningValues()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "relatorio", new Dictionary<string, string>
        {
            ["nome_solicitante"] = "Fulano", ["finalidade"] = "Avaliar", ["descricao_demanda"] = "Queixa",
            ["procedimento"] = "Entrevistas", ["referencial_teorico"] = "TCC",
            ["pessoas_ouvidas_numero_de_encontros_e_duracao"] = "1 pessoa", ["analise"] = "Análise", ["conclusao"] = "Fim",
            ["prazo_validade"] = "60 dias",
        }, ["afirmacao_sem_fonte_ou_sustentacao", "descricao_literal_das_sessoes"]);
        await using var db = seed.CreateDbContext();
        (await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct)).Outcome.Should().Be(DocumentOutcome.Ok);
        var signedName = await db.Providers.AsNoTracking().Where(p => p.Id == seed.ProviderId).Select(p => p.Name).SingleAsync(Ct);

        // Act
        await seed.ExecuteSqlAsync(
            """UPDATE "Providers" SET "PracticeCity" = 'Curitiba', "Name" = 'Outro Nome', "CrpNumber" = '99999' WHERE "Id" = @id""",
            Ct, ("id", seed.ProviderId));
        await seed.ExecuteSqlAsync("""UPDATE "Patients" SET "FirstName" = 'Bia', "LastName" = 'Lima' WHERE "Id" = @id""", Ct, ("id", patientId));
        var detail = await ClinicalDocumentService.GetAsync(db, seed.ProviderId, id, Ct);
        var document = await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        var ruleset = await db.ConformityRulesets.AsNoTracking().SingleAsync(r => r.Id == document.RulesetId, Ct);
        var patient = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == patientId, Ct);
        var provider = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == seed.ProviderId, Ct);
        var model = DocumentPdfModel.From(document, ruleset, patient, provider);

        // Assert
        document.SignedCity.Should().Be("São Paulo");
        document.SignedByName.Should().Be(signedName);
        document.SignedPatientName.Should().Be("Ana Souza");
        detail!.PatientName.Should().Be("Ana Souza");
        detail.SignedCity.Should().Be("São Paulo");
        detail.SignedByName.Should().Be(signedName);
        detail.SignedByCrp.Should().Be("12345/06");
        detail.HasPracticeCity.Should().BeTrue();
        model.PracticeCity.Should().Be("São Paulo");
        model.ProviderName.Should().Be(signedName);
        model.Identification.Should().Contain(new DocumentPdfLine("Autor(a)", signedName));
        model.PatientName.Should().Be("Ana Souza");
        model.Crp.Should().Be("12345/06");
    }

    [Fact]
    public async Task Correct_SignedDocument_CreatesDraftCopyAndSupersedesOriginal()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "atestado", CompleteAtestado, AtestadoConfirmations);
        await using var db = seed.CreateDbContext();
        await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);
        var signed = await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        var signedBody = signed.Body;
        var signedCrp = signed.SignedByCrp;

        // Act
        var result = await ClinicalDocumentService.CorrectAsync(db, seed.ProviderId, id, Now.AddDays(1), Ct);
        var again = await ClinicalDocumentService.CorrectAsync(db, seed.ProviderId, id, Now.AddDays(1), Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        again.Outcome.Should().Be(DocumentOutcome.Locked);
        var original = await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
        original.SupersededBy.Should().Be(result.Id);
        original.SignedAt.Should().Be(Now);
        original.Body.Should().Be(signedBody);
        original.SignedByCrp.Should().Be(signedCrp);
        original.Confirmations.Should().Equal("informacao_alem_do_solicitado");
        var copy = await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == result.Id, Ct);
        copy.SignedAt.Should().BeNull();
        copy.Fields.Should().BeEquivalentTo(CompleteAtestado);
        copy.Confirmations.Should().BeEmpty();
        copy.Kind.Should().Be("atestado");
        copy.RulesetId.Should().Be(original.RulesetId);
        (await db.AuditEntries.AsNoTracking().AnyAsync(a => a.DocumentId == id && a.Action == "document_corrected", Ct)).Should().BeTrue();
        (await db.AuditEntries.AsNoTracking().AnyAsync(a => a.DocumentId == copy.Id && a.Action == "document_created", Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Correct_Draft_ReturnsLocked()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "declaracao");
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.CorrectAsync(db, seed.ProviderId, id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Locked);
    }

    [Fact]
    public async Task ListAndGet_AfterCorrection_ReportDraftSignedAndSupersededStatuses()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var first = await CreateDraftAsync(seed, patientId, "atestado", CompleteAtestado, AtestadoConfirmations);
        var plain = await CreateDraftAsync(seed, patientId, "declaracao");
        await using var db = seed.CreateDbContext();
        await ClinicalDocumentService.SignAsync(db, seed.ProviderId, first, false, Now, Ct);
        var correction = await ClinicalDocumentService.CorrectAsync(db, seed.ProviderId, first, Now, Ct);

        // Act
        var list = await ClinicalDocumentService.ListAsync(db, seed.ProviderId, patientId, Ct);
        var detail = await ClinicalDocumentService.GetAsync(db, seed.ProviderId, first, Ct);
        var foreign = await ClinicalDocumentService.GetAsync(db, await seed.AddProviderAsync(Ct), first, Ct);
        var foreignList = await ClinicalDocumentService.ListAsync(db, await seed.AddProviderAsync(Ct), patientId, Ct);

        // Assert
        list!.ToDictionary(d => d.Id, d => d.Status).Should().BeEquivalentTo(new Dictionary<Guid, string>
        {
            [first] = "superseded", [correction.Id!.Value] = "draft", [plain] = "draft",
        });
        list.Single(d => d.Id == first).SupersededBy.Should().Be(correction.Id);
        detail!.Status.Should().Be("superseded");
        detail.PatientName.Should().Be("Ana Souza");
        detail.Title.Should().Be("Atestado psicológico");
        detail.SignedByCrp.Should().Be("12345/06");
        detail.ProviderHasCrp.Should().BeTrue();
        detail.HasPracticeCity.Should().BeTrue();
        detail.Check.ElementsComplete.Should().BeTrue();
        foreign.Should().BeNull();
        foreignList.Should().BeNull();
    }

    [Fact]
    public async Task Save_MultiTeam_TrimsDropsEmptyMembersAndAudits()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "relatorio_multiprofissional");
        await using var db = seed.CreateDbContext();
        TeamMember[] team = [new(" Rita ", "Psiquiatria", "CRM 1", "A"), new("", " ", "", "")];

        // Act
        var result = await ClinicalDocumentService.SaveAsync(db, seed.ProviderId, id, new Dictionary<string, string>(), [], team, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        var detail = await ClinicalDocumentService.GetAsync(db, seed.ProviderId, id, Ct);
        detail!.Team.Should().Equal(new TeamMember("Rita", "Psiquiatria", "CRM 1", "A"));
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.DocumentId == id && a.FieldChanged == "team", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Save_TeamOnLaudo_ReturnsTeamNotAllowedAndStoresNothing()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "laudo");
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SaveAsync(db, seed.ProviderId, id, new Dictionary<string, string>(), [], RitaTeam, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.TeamNotAllowed);
        (await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct)).Team.Should().BeEmpty();
        (await db.AuditEntries.AsNoTracking().AnyAsync(a => a.DocumentId == id && a.FieldChanged == "team", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Save_SameTeamAgain_WritesNoAuditEntry()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "relatorio_multiprofissional", new(), [], RitaTeam);
        await using var db = seed.CreateDbContext();
        var before = await db.AuditEntries.AsNoTracking().CountAsync(a => a.DocumentId == id, Ct);

        // Act
        var result = await ClinicalDocumentService.SaveAsync(db, seed.ProviderId, id, new Dictionary<string, string>(), [], RitaTeam, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        (await db.AuditEntries.AsNoTracking().CountAsync(a => a.DocumentId == id, Ct)).Should().Be(before);
    }

    [Fact]
    public async Task Sign_CompleteLaudo_SignsWithNumberedBody()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "laudo", CompleteLaudo, LaudoConfirmations);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        (await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct)).Body.Should().StartWith("1. Identificação\nPessoa atendida: ");
    }

    [Fact]
    public async Task Sign_CompleteParecer_Signs()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "parecer", CompleteParecer, ParecerConfirmations);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
    }

    [Fact]
    public async Task Sign_ParecerWithoutTitulacaoBlocking_ReturnsMissingElements()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var fields = new Dictionary<string, string>(CompleteParecer);
        fields.Remove("nome_autor_inscricao_crp_e_titulacao");
        var id = await CreateDraftAsync(seed, patientId, "parecer", fields, ParecerConfirmations);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.MissingElements);
        result.Missing.Should().Contain("nome_autor_inscricao_crp_e_titulacao");
    }

    [Fact]
    public async Task Sign_MultiWithoutTeamBlocking_ReturnsMissingElementsWithTeamCodes()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "relatorio_multiprofissional", CompleteMulti, MultiConfirmations);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.MissingElements);
        result.Missing.Should().Contain(
            "nomes_autores_categorias_e_registros_profissionais", "nomes_profissionais", "inscricoes_profissionais",
            "analise_identificada_por_profissional_e_categoria");
    }

    [Fact]
    public async Task Sign_CompleteMulti_SignsAndBodyHasMemberAnalysis()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "relatorio_multiprofissional", CompleteMulti, MultiConfirmations, RitaTeam);
        await using var db = seed.CreateDbContext();

        // Act
        var result = await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        var body = (await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct)).Body;
        body.Should().Contain("Análise — Rita (Psiquiatria)").And.Contain("Análise da Rita");
    }

    [Fact]
    public async Task Save_MultiAfterSign_ReturnsLocked()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "relatorio_multiprofissional", CompleteMulti, MultiConfirmations, RitaTeam);
        await using var db = seed.CreateDbContext();
        (await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct)).Outcome.Should().Be(DocumentOutcome.Ok);

        // Act
        var result = await ClinicalDocumentService.SaveAsync(
            db, seed.ProviderId, id, CompleteMulti, MultiConfirmations, [new("Outro", "Fono", "CRFa 2", "B")], Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Locked);
        (await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == id, Ct)).Team.Should().Equal(RitaTeam);
    }

    [Fact]
    public async Task Correct_SignedMulti_CopiesTeamToNewDraft()
    {
        // Arrange
        var (seed, patientId) = await SeedAsync();
        await using var _ = seed;
        var id = await CreateDraftAsync(seed, patientId, "relatorio_multiprofissional", CompleteMulti, MultiConfirmations, RitaTeam);
        await using var db = seed.CreateDbContext();
        await ClinicalDocumentService.SignAsync(db, seed.ProviderId, id, false, Now, Ct);

        // Act
        var result = await ClinicalDocumentService.CorrectAsync(db, seed.ProviderId, id, Now, Ct);

        // Assert
        result.Outcome.Should().Be(DocumentOutcome.Ok);
        (await db.ClinicalDocuments.AsNoTracking().SingleAsync(d => d.Id == result.Id, Ct)).Team.Should().Equal(RitaTeam);
    }
}
