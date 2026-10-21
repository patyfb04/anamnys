using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using FluentAssertions;

namespace Anamnys.Tests.Documents;

// Pure tests: no AppHost. Element lists copied from design/migrations/2026-10-19-documents.sql.
public class DocumentConformityTests
{
    private static readonly string[] DeclaracaoElements =
    [
        "titulo_declaracao", "nome_pessoa_atendida", "finalidade",
        "local_dias_horarios_e_duracao_do_acompanhamento", "local_emissao", "data_emissao", "carimbo",
        "nome_psicologo", "inscricao_crp", "assinatura", "laudas_numeradas_rubricadas", "prazo_validade",
    ];

    private static readonly string[] AtestadoElements =
    [
        "titulo_atestado_psicologico", "nome_pessoa_ou_instituicao_atendida", "nome_solicitante", "finalidade",
        "descricao_condicoes_psicologicas", "local_emissao", "data_emissao", "carimbo", "nome_psicologo",
        "inscricao_crp", "laudas_numeradas_rubricadas", "assinatura", "prazo_validade",
    ];

    private static readonly string[] RelatorioElements =
    [
        "identificacao", "titulo_relatorio_psicologico", "nome_pessoa_ou_instituicao_atendida", "nome_solicitante",
        "finalidade", "nome_autor_e_inscricao_crp", "descricao_demanda", "procedimento", "referencial_teorico",
        "pessoas_ouvidas_numero_de_encontros_e_duracao", "analise", "conclusao", "local_emissao", "data_emissao",
        "carimbo", "nome_psicologo", "inscricao_crp", "laudas_numeradas_rubricadas", "assinatura", "prazo_validade",
    ];

    internal static readonly string[] LaudoElements =
    [
        "secoes_em_forma_de_itens", "identificacao", "titulo_laudo_psicologico", "nome_pessoa_ou_instituicao_atendida",
        "nome_solicitante", "finalidade", "nome_autor_e_inscricao_crp", "descricao_demanda", "procedimento",
        "referencial_teorico_metodologico", "pessoas_ouvidas_numero_de_encontros_e_duracao", "analise", "conclusao",
        "local_emissao", "data_emissao", "carimbo", "nome_psicologo", "inscricao_crp", "laudas_numeradas_rubricadas",
        "assinatura", "referencias", "prazo_validade",
    ];

    internal static readonly string[] ParecerElements =
    [
        "secoes_em_forma_de_itens", "identificacao", "titulo_parecer_psicologico",
        "nome_pessoa_ou_instituicao_objeto_do_questionamento", "nome_solicitante", "finalidade",
        "nome_autor_inscricao_crp_e_titulacao", "descricao_demanda", "analise", "conclusao", "local_emissao",
        "data_emissao", "carimbo", "nome_psicologo", "inscricao_crp", "laudas_numeradas_rubricadas", "assinatura",
        "referencias", "prazo_validade",
    ];

    internal static readonly string[] MultiElements =
    [
        "identificacao", "titulo_relatorio_multiprofissional", "nome_pessoa_ou_instituicao_atendida", "nome_solicitante",
        "finalidade", "nomes_autores_categorias_e_registros_profissionais", "descricao_demanda", "procedimento",
        "procedimentos_privativos_da_psicologia_em_secao_separada", "analise",
        "analise_identificada_por_profissional_e_categoria", "conclusao", "local_emissao", "data_emissao", "carimbo",
        "nomes_profissionais", "inscricoes_profissionais", "laudas_numeradas_rubricadas", "assinatura_do_psicologo",
        "prazo_validade",
    ];

    private static readonly Dictionary<string, string[]> ElementsByKind = new()
    {
        ["declaracao"] = DeclaracaoElements,
        ["atestado"] = AtestadoElements,
        ["relatorio"] = RelatorioElements,
        ["laudo"] = LaudoElements,
        ["parecer"] = ParecerElements,
        ["relatorio_multiprofissional"] = MultiElements,
    };

    private static readonly Dictionary<string, string[]> ProhibitionsByKind = new()
    {
        ["declaracao"] = ["sintomas_situacoes_ou_estados_psicologicos"],
        ["atestado"] = ["quebra_de_paragrafo_sem_preenchimento", "informacao_alem_do_solicitado"],
        ["relatorio"] = ["afirmacao_sem_fonte_ou_sustentacao", "descricao_literal_das_sessoes"],
        ["laudo"] = ["descricao_literal_das_sessoes", "afirmacao_sem_fonte_ou_sustentacao"],
        ["parecer"] = ["resultado_de_avaliacao_ou_intervencao_psicologica"],
        ["relatorio_multiprofissional"] = ["afirmacao_sem_fonte_ou_sustentacao", "descricao_literal_das_sessoes"],
    };

    private static readonly string[] SystemCrpElements = ["nome_psicologo", "inscricao_crp", "nome_autor_e_inscricao_crp"];

    private static readonly Dictionary<string, string> KindArticle = new()
    {
        ["declaracao"] = "Art. 9º",
        ["atestado"] = "Art. 10",
        ["relatorio"] = "Art. 11",
        ["laudo"] = "Art. 13",
        ["parecer"] = "Art. 14",
        ["relatorio_multiprofissional"] = "Art. 12",
    };

    public static TheoryData<string> Kinds => ["declaracao", "atestado", "relatorio"];

    // Spec section 3, hard-coded: the provider-written elements of each kind.
    private static readonly Dictionary<string, string[]> ExpectedProviderElements = new()
    {
        ["declaracao"] = ["finalidade", "local_dias_horarios_e_duracao_do_acompanhamento", "prazo_validade"],
        ["atestado"] = ["nome_solicitante", "finalidade", "descricao_condicoes_psicologicas", "prazo_validade"],
        ["relatorio"] =
        [
            "nome_solicitante", "finalidade", "descricao_demanda", "procedimento", "referencial_teorico",
            "pessoas_ouvidas_numero_de_encontros_e_duracao", "analise", "conclusao", "prazo_validade",
        ],
        ["laudo"] =
        [
            "nome_solicitante", "finalidade", "descricao_demanda", "procedimento", "referencial_teorico_metodologico",
            "pessoas_ouvidas_numero_de_encontros_e_duracao", "analise", "conclusao", "referencias", "prazo_validade",
        ],
        ["parecer"] =
        [
            "nome_solicitante", "finalidade", "nome_autor_inscricao_crp_e_titulacao", "descricao_demanda", "analise",
            "conclusao", "referencias", "prazo_validade",
        ],
        ["relatorio_multiprofissional"] =
        [
            "nome_solicitante", "finalidade", "descricao_demanda", "procedimento",
            "procedimentos_privativos_da_psicologia_em_secao_separada", "analise", "conclusao", "prazo_validade",
        ],
    };

    // Spec section 3, hard-coded: origin of every non-provider element.
    private static readonly Dictionary<string, ElementOrigin> ExpectedNonProviderOrigins = new()
    {
        ["titulo_declaracao"] = ElementOrigin.Pdf,
        ["titulo_atestado_psicologico"] = ElementOrigin.Pdf,
        ["titulo_relatorio_psicologico"] = ElementOrigin.Pdf,
        ["nome_pessoa_atendida"] = ElementOrigin.System,
        ["nome_pessoa_ou_instituicao_atendida"] = ElementOrigin.System,
        ["identificacao"] = ElementOrigin.System,
        ["nome_psicologo"] = ElementOrigin.System,
        ["inscricao_crp"] = ElementOrigin.System,
        ["nome_autor_e_inscricao_crp"] = ElementOrigin.System,
        ["local_emissao"] = ElementOrigin.System,
        ["data_emissao"] = ElementOrigin.System,
        ["carimbo"] = ElementOrigin.Pdf,
        ["assinatura"] = ElementOrigin.Pdf,
        ["laudas_numeradas_rubricadas"] = ElementOrigin.Pdf,
        ["titulo_laudo_psicologico"] = ElementOrigin.Pdf,
        ["titulo_parecer_psicologico"] = ElementOrigin.Pdf,
        ["titulo_relatorio_multiprofissional"] = ElementOrigin.Pdf,
        ["secoes_em_forma_de_itens"] = ElementOrigin.Pdf,
        ["assinatura_do_psicologo"] = ElementOrigin.Pdf,
        ["nome_pessoa_ou_instituicao_objeto_do_questionamento"] = ElementOrigin.System,
        ["nomes_autores_categorias_e_registros_profissionais"] = ElementOrigin.System,
        ["analise_identificada_por_profissional_e_categoria"] = ElementOrigin.System,
        ["nomes_profissionais"] = ElementOrigin.System,
        ["inscricoes_profissionais"] = ElementOrigin.System,
    };

    private static string[] ProviderElements(string kind) => ExpectedProviderElements[kind];

    private static string[] CatalogProviderElements(string kind) =>
        [.. ElementsByKind[kind].Where(c => DocumentCatalog.Element(kind, c).Origin == ElementOrigin.Provider)];

    private static ConformityInput Complete(string kind) => new(
        kind,
        ElementsByKind[kind],
        ProhibitionsByKind[kind],
        ProviderElements(kind).ToDictionary(c => c, _ => "texto"),
        ProhibitionsByKind[kind],
        ProviderHasCrp: true,
        HasPracticeCity: true);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Check_CompleteInput_PassesWithNothingMissing(string kind)
    {
        // Arrange
        var input = Complete(kind);

        // Act
        var result = DocumentConformity.Check(input);

        // Assert
        result.ElementsComplete.Should().BeTrue();
        result.ConfirmationsComplete.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Check_EachProviderElementAlone_IsReportedWhenEmptyOrAbsent(string kind)
    {
        // Arrange
        var complete = Complete(kind);

        foreach (var code in ProviderElements(kind))
        {
            var blank = new Dictionary<string, string>(complete.Fields) { [code] = "   " };
            var absent = complete.Fields.Where(f => f.Key != code).ToDictionary();

            // Act
            var blankResult = DocumentConformity.Check(complete with { Fields = blank });
            var absentResult = DocumentConformity.Check(complete with { Fields = absent });

            // Assert
            blankResult.MissingElements.Should().Equal(code);
            absentResult.MissingElements.Should().Equal(code);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Check_ProviderWithoutCrp_ReportsCrpElementsOfTheKind(string kind)
    {
        // Arrange
        var input = Complete(kind) with { ProviderHasCrp = false };

        // Act
        var result = DocumentConformity.Check(input);

        // Assert
        result.MissingElements.Should().BeEquivalentTo(ElementsByKind[kind].Where(SystemCrpElements.Contains));
        result.MissingElements.Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Check_NoPracticeCity_ReportsOnlyLocalEmissao(string kind)
    {
        // Arrange
        var input = Complete(kind) with { HasPracticeCity = false };

        // Act
        var result = DocumentConformity.Check(input);

        // Assert
        result.MissingElements.Should().Equal("local_emissao");
    }

    [Fact]
    public void Check_NoCrpAndNoCity_ReportsBothInRulesetOrder()
    {
        // Arrange
        var input = Complete("declaracao") with { ProviderHasCrp = false, HasPracticeCity = false };

        // Act
        var result = DocumentConformity.Check(input);

        // Assert
        result.MissingElements.Should().Equal("local_emissao", "nome_psicologo", "inscricao_crp");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Check_EachConfirmationRequired_ReportedWhenNotGiven(string kind)
    {
        // Arrange
        var required = ProhibitionsByKind[kind]
            .Where(c => !DocumentCatalog.Prohibition(kind, c).ByConstruction).ToArray();

        foreach (var code in required)
        {
            var input = Complete(kind) with { Confirmations = required.Where(c => c != code).ToArray() };

            // Act
            var result = DocumentConformity.Check(input);

            // Assert
            result.MissingConfirmations.Should().Equal(code);
            result.ConfirmationsComplete.Should().BeFalse();
        }
    }

    [Fact]
    public void Check_NoConfirmationsGiven_AtestadoRequiresOnlyInformacaoAlemDoSolicitado()
    {
        // Arrange
        var input = Complete("atestado") with { Confirmations = [] };

        // Act
        var result = DocumentConformity.Check(input);

        // Assert
        result.MissingConfirmations.Should().Equal("informacao_alem_do_solicitado");
    }

    [Fact]
    public void Prohibition_QuebraDeParagrafo_IsByConstructionWithoutText()
    {
        // Act
        var info = DocumentCatalog.Prohibition("atestado", "quebra_de_paragrafo_sem_preenchimento");

        // Assert
        info.ByConstruction.Should().BeTrue();
        info.ConfirmationText.Should().BeNull();
    }

    private static readonly TeamMember FullMember = new("Rita Lima", "Psiquiatria", "CRM 12345", "Análise da psiquiatria.");

    private static ConformityResult CheckFull(string kind, IReadOnlyList<TeamMember>? team, bool crp = true, Dictionary<string, string>? fields = null)
    {
        var elements = ElementsByKind[kind];
        var allFields = fields ?? elements
            .Where(code => DocumentCatalog.Element(kind, code).Origin == ElementOrigin.Provider)
            .ToDictionary(code => code, _ => "texto");
        return DocumentConformity.Check(new ConformityInput(
            kind, elements, ProhibitionsByKind[kind], allFields, ProhibitionsByKind[kind], crp, true, team));
    }

    [Theory]
    [InlineData("laudo")]
    [InlineData("parecer")]
    [InlineData("relatorio_multiprofissional")]
    public void Check_NewKindComplete_ReportsNothingMissing(string kind)
    {
        // Arrange
        IReadOnlyList<TeamMember>? team = kind == "relatorio_multiprofissional" ? [FullMember] : null;

        // Act
        var result = CheckFull(kind, team);

        // Assert
        result.MissingElements.Should().BeEmpty();
        result.MissingConfirmations.Should().BeEmpty();
    }

    [Fact]
    public void Check_ParecerWithoutCrp_ReportsTitulacaoMissing()
    {
        // Act
        var result = CheckFull("parecer", null, crp: false);

        // Assert
        result.MissingElements.Should().Contain("nome_autor_inscricao_crp_e_titulacao");
    }

    [Fact]
    public void Check_ParecerSubject_IsSystemAndNeverMissing()
    {
        // Act
        var info = DocumentCatalog.Element("parecer", "nome_pessoa_ou_instituicao_objeto_do_questionamento");

        // Assert
        info.Origin.Should().Be(ElementOrigin.System);
    }

    [Fact]
    public void Check_MultiEmptyTeam_ReportsAuthorsAndAnalysisMissing()
    {
        // Act
        var result = CheckFull("relatorio_multiprofissional", []);

        // Assert
        result.MissingElements.Should().BeEquivalentTo([
            "nomes_autores_categorias_e_registros_profissionais", "nomes_profissionais", "inscricoes_profissionais",
            "analise_identificada_por_profissional_e_categoria",
        ]);
    }

    [Fact]
    public void Check_MultiMemberWithoutRegistry_ReportsOnlyAuthorElements()
    {
        // Act
        var result = CheckFull("relatorio_multiprofissional", [FullMember with { Registry = " " }]);

        // Assert
        result.MissingElements.Should().BeEquivalentTo([
            "nomes_autores_categorias_e_registros_profissionais", "nomes_profissionais", "inscricoes_profissionais",
        ]);
    }

    [Fact]
    public void Check_MultiMemberWithoutAnalysis_ReportsOnlyIdentifiedAnalysis()
    {
        // Act
        var result = CheckFull("relatorio_multiprofissional", [FullMember, FullMember with { Analysis = "" }]);

        // Assert
        result.MissingElements.Should().Equal("analise_identificada_por_profissional_e_categoria");
    }

    [Fact]
    public void Check_MultiWithoutCrp_ReportsAuthorElements()
    {
        // Act
        var result = CheckFull("relatorio_multiprofissional", [FullMember], crp: false);

        // Assert
        result.MissingElements.Should().Contain(["nomes_profissionais", "inscricoes_profissionais"]);
    }

    [Theory]
    [InlineData("relatorio", "afirmacao_sem_fonte_ou_sustentacao", "Confirmo que toda afirmação do relatório tem fonte ou sustentação técnica.")]
    [InlineData("relatorio", "descricao_literal_das_sessoes", "Confirmo que o relatório não transcreve literalmente as sessões.")]
    [InlineData("laudo", "afirmacao_sem_fonte_ou_sustentacao", "Confirmo que toda afirmação do laudo tem fonte ou sustentação técnica.")]
    [InlineData("laudo", "descricao_literal_das_sessoes", "Confirmo que o laudo não transcreve literalmente as sessões.")]
    [InlineData("relatorio_multiprofissional", "afirmacao_sem_fonte_ou_sustentacao", "Confirmo que toda afirmação do relatório multiprofissional tem fonte ou sustentação técnica.")]
    [InlineData("relatorio_multiprofissional", "descricao_literal_das_sessoes", "Confirmo que o relatório multiprofissional não transcreve literalmente as sessões.")]
    [InlineData("parecer", "resultado_de_avaliacao_ou_intervencao_psicologica", "Confirmo que este parecer não resulta de processo de avaliação ou intervenção psicológica.")]
    [InlineData("declaracao", "sintomas_situacoes_ou_estados_psicologicos", "Confirmo que esta declaração não descreve sintomas, situações ou estados psicológicos.")]
    [InlineData("atestado", "informacao_alem_do_solicitado", "Confirmo que o atestado se restringe à informação solicitada.")]
    public void Prohibition_KindAndCode_ReturnsKindSpecificText(string kind, string code, string expected)
    {
        // Act
        var info = DocumentCatalog.Prohibition(kind, code);

        // Assert
        info.ConfirmationText.Should().Be(expected);
        info.ByConstruction.Should().BeFalse();
    }

    [Theory]
    [InlineData("laudo")]
    [InlineData("parecer")]
    [InlineData("relatorio_multiprofissional")]
    public void Check_NewKindNoConfirmationsGiven_ReportsEveryProhibition(string kind)
    {
        // Arrange
        IReadOnlyList<TeamMember>? team = kind == "relatorio_multiprofissional" ? [FullMember] : null;
        var elements = ElementsByKind[kind];
        var fields = elements
            .Where(code => DocumentCatalog.Element(kind, code).Origin == ElementOrigin.Provider)
            .ToDictionary(code => code, _ => "texto");

        // Act
        var result = DocumentConformity.Check(new ConformityInput(
            kind, elements, ProhibitionsByKind[kind], fields, [], true, true, team));

        // Assert
        result.MissingElements.Should().BeEmpty();
        result.MissingConfirmations.Should().BeEquivalentTo(ProhibitionsByKind[kind]);
        result.ConfirmationsComplete.Should().BeFalse();
    }

    [Fact]
    public void Check_UnknownElementCode_IsTreatedAsProviderElement()
    {
        // Arrange
        var input = Complete("declaracao") with { Elements = [.. DeclaracaoElements, "codigo_desconhecido"] };

        // Act
        var result = DocumentConformity.Check(input);
        var info = DocumentCatalog.Element("declaracao", "codigo_desconhecido");

        // Assert
        result.MissingElements.Should().Equal("codigo_desconhecido");
        info.Origin.Should().Be(ElementOrigin.Provider);
        info.Label.Should().Be("codigo_desconhecido");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Element_SeededRulesets_HaveLabelAndHintNamingTheArticle(string kind)
    {
        foreach (var code in ElementsByKind[kind])
        {
            // Act
            var info = DocumentCatalog.Element(kind, code);

            // Assert
            info.Code.Should().Be(code);
            info.Label.Should().NotBeNullOrWhiteSpace().And.NotBe(code);
            info.Hint.Should().NotBeNullOrWhiteSpace();
            var expectedCitation = code switch
            {
                "prazo_validade" => "Art. 17",
                "laudas_numeradas_rubricadas" => "Art. 5º §8º",
                _ => KindArticle[kind],
            };
            info.Hint.Should().EndWith($"({expectedCitation})");
            if (code != "laudas_numeradas_rubricadas")
                info.Hint.Should().NotContain("§");
        }
    }

    [Fact]
    public void Element_Origins_FollowSpec()
    {
        foreach (var kind in ElementsByKind.Keys)
        {
            var providerCodes = ElementsByKind[kind].Where(c => !ExpectedNonProviderOrigins.ContainsKey(c));

            CatalogProviderElements(kind).Should().BeEquivalentTo(ExpectedProviderElements[kind]);
            providerCodes.Should().BeEquivalentTo(ExpectedProviderElements[kind]);
            foreach (var (code, origin) in ExpectedNonProviderOrigins.Where(o => ElementsByKind[kind].Contains(o.Key)))
                DocumentCatalog.Element(kind, code).Origin.Should().Be(origin, $"{kind}/{code}");
        }
    }

    [Fact]
    public void KindTitle_AllSixKinds_HaveTitles()
    {
        DocumentCatalog.KindTitle("declaracao").Should().Be("Declaração");
        DocumentCatalog.KindTitle("atestado").Should().Be("Atestado psicológico");
        DocumentCatalog.KindTitle("relatorio").Should().Be("Relatório psicológico");
        DocumentCatalog.KindTitle("relatorio_multiprofissional").Should().Be("Relatório multiprofissional");
        DocumentCatalog.KindTitle("laudo").Should().Be("Laudo psicológico");
        DocumentCatalog.KindTitle("parecer").Should().Be("Parecer psicológico");
    }

    [Fact]
    public void AvailableKinds_AreAllSixKinds()
    {
        DocumentCatalog.AvailableKinds.Should().Equal("declaracao", "atestado", "relatorio", "relatorio_multiprofissional", "laudo", "parecer");
    }
}
