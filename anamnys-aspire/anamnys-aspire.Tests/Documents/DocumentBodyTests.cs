using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using FluentAssertions;

namespace Anamnys.Tests.Documents;

public class DocumentBodyTests
{
    private static readonly string[] DeclaracaoElements =
        ["titulo_declaracao", "nome_pessoa_atendida", "finalidade", "local_dias_horarios_e_duracao_do_acompanhamento", "prazo_validade"];

    private static readonly string[] AtestadoElements =
        ["nome_pessoa_ou_instituicao_atendida", "nome_solicitante", "finalidade", "descricao_condicoes_psicologicas", "prazo_validade"];

    private static readonly string[] RelatorioElements =
        ["identificacao", "nome_solicitante", "finalidade", "descricao_demanda", "procedimento", "prazo_validade"];

    private static string[] LaudoElements => DocumentConformityTests.LaudoElements;

    private static string[] ParecerElements => DocumentConformityTests.ParecerElements;

    private static string[] MultiElements => DocumentConformityTests.MultiElements;

    [Fact]
    public void Assemble_Declaracao_JoinsFieldsInRulesetOrderWithValidityLast()
    {
        // Arrange
        var fields = new Dictionary<string, string>
        {
            ["prazo_validade"] = "90 dias.",
            ["local_dias_horarios_e_duracao_do_acompanhamento"] = "Consultório, terças.",
            ["finalidade"] = "Comprovar comparecimento.",
        };

        // Act
        var body = DocumentBody.Assemble("declaracao", DeclaracaoElements, fields);

        // Assert
        body.Should().Be("Finalidade: Comprovar comparecimento.\n\nLocal, dias, horários e duração do acompanhamento: Consultório, terças.\n\nPrazo de validade: 90 dias.");
    }

    [Fact]
    public void Assemble_Declaracao_KeepsLineBreaksTypedInsideAValue()
    {
        // Arrange
        var fields = new Dictionary<string, string>
        {
            ["finalidade"] = "Linha um.\nLinha dois.",
            ["prazo_validade"] = "90 dias.",
        };

        // Act
        var body = DocumentBody.Assemble("declaracao", DeclaracaoElements, fields);

        // Assert
        body.Should().Be("Finalidade: Linha um.\nLinha dois.\n\nPrazo de validade: 90 dias.");
    }

    [Fact]
    public void Assemble_Atestado_ReplacesLineBreaksWithSpaces()
    {
        // Arrange
        var fields = new Dictionary<string, string>
        {
            ["nome_solicitante"] = "Escola X",
            ["finalidade"] = "Afastamento",
            ["descricao_condicoes_psicologicas"] = "Linha um.\r\n\r\nLinha dois.\nLinha três.",
            ["prazo_validade"] = "30 dias.",
        };

        // Act
        var body = DocumentBody.Assemble("atestado", AtestadoElements, fields);

        // Assert
        body.Should().Be("Nome do solicitante: Escola X. Finalidade: Afastamento. "
            + "Descrição das condições psicológicas: Linha um. Linha dois. Linha três. Prazo de validade: 30 dias.");
        body.Should().NotContain("\n");
    }

    [Fact]
    public void Assemble_Relatorio_BuildsLabelledBlocksWithValidityLast()
    {
        // Arrange
        var fields = new Dictionary<string, string>
        {
            ["prazo_validade"] = "60 dias.",
            ["procedimento"] = "Entrevistas.",
            ["descricao_demanda"] = "Queixa.",
        };

        // Act
        var body = DocumentBody.Assemble("relatorio", RelatorioElements, fields);

        // Assert
        body.Should().Be("Descrição da demanda\nQueixa.\n\nProcedimento\nEntrevistas.\n\nPrazo de validade\n60 dias.");
    }

    [Fact]
    public void Assemble_EmptyFields_ReturnsEmptyString()
    {
        // Arrange
        var fields = new Dictionary<string, string>();

        // Act
        var body = DocumentBody.Assemble("declaracao", DeclaracaoElements, fields);

        // Assert
        body.Should().BeEmpty();
    }

    [Fact]
    public void Build_Laudo_NumbersItemsAndKeepsSubParagraphsInsideProcedimento()
    {
        // Arrange
        var fields = new Dictionary<string, string>
        {
            ["nome_solicitante"] = "Juízo", ["finalidade"] = "Instruir processo", ["descricao_demanda"] = "Demanda",
            ["procedimento"] = "Entrevistas", ["referencial_teorico_metodologico"] = "TCC",
            ["pessoas_ouvidas_numero_de_encontros_e_duracao"] = "Mãe, 3 encontros", ["analise"] = "Análise",
            ["conclusao"] = "Conclusão", ["referencias"] = "Ref A\nRef B", ["prazo_validade"] = "90 dias",
        };

        // Act
        var sections = DocumentSections.Build("laudo", LaudoElements, fields, [], "Dra. Paula");

        // Assert
        sections.Select(s => s.Heading).Should().Equal(
            "2. Descrição da demanda", "3. Procedimento", "Referencial teórico-metodológico",
            "Pessoas ouvidas, número de encontros e duração", "4. Análise", "5. Conclusão", "6. Referências",
            "Prazo de validade");
        sections.Single(s => s.Heading == "6. Referências").Text.Should().Be("Ref A\nRef B");
    }

    [Fact]
    public void Build_Parecer_NumbersFiveItemsWithoutProcedimento()
    {
        // Arrange
        var fields = new Dictionary<string, string>
        {
            ["descricao_demanda"] = "D", ["analise"] = "A", ["conclusao"] = "C", ["referencias"] = "R",
            ["prazo_validade"] = "30 dias", ["nome_solicitante"] = "S", ["finalidade"] = "F",
            ["nome_autor_inscricao_crp_e_titulacao"] = "Especialista em Psicologia Jurídica",
        };

        // Act
        var sections = DocumentSections.Build("parecer", ParecerElements, fields, [], "Dra. Paula");

        // Assert
        sections.Select(s => s.Heading).Should().Equal(
            "2. Descrição da demanda", "3. Análise", "4. Conclusão", "5. Referências", "Prazo de validade");
    }

    [Fact]
    public void Build_LaudoMissingProcedimento_NumbersPrintedItemsConsecutively()
    {
        // Arrange
        var fields = new Dictionary<string, string> { ["descricao_demanda"] = "D", ["analise"] = "A", ["prazo_validade"] = "1 ano" };

        // Act
        var sections = DocumentSections.Build("laudo", LaudoElements, fields, [], "Dra. Paula");

        // Assert
        sections.Select(s => s.Heading).Should().Equal("2. Descrição da demanda", "3. Análise", "Prazo de validade");
    }

    [Fact]
    public void Build_LaudoSubItemsWithoutProcedimento_KeepsProcedimentoHeading()
    {
        // Arrange
        var fields = new Dictionary<string, string>
        {
            ["descricao_demanda"] = "D", ["referencial_teorico_metodologico"] = "TCC",
            ["pessoas_ouvidas_numero_de_encontros_e_duracao"] = "Mãe, 3 encontros", ["analise"] = "A",
        };

        // Act
        var sections = DocumentSections.Build("laudo", LaudoElements, fields, [], "Dra. Paula");

        // Assert
        sections.Select(s => s.Heading).Should().Equal(
            "2. Descrição da demanda", "3. Procedimento", "Referencial teórico-metodológico",
            "Pessoas ouvidas, número de encontros e duração", "4. Análise");
        sections.Single(s => s.Heading == "3. Procedimento").Text.Should().BeEmpty();
    }

    [Fact]
    public void Build_Multi_HeadsEachAnalysisWithProfessionalAndCategory()
    {
        // Arrange
        var fields = new Dictionary<string, string>
        {
            ["descricao_demanda"] = "D", ["procedimento"] = "P",
            ["procedimentos_privativos_da_psicologia_em_secao_separada"] = "Testes", ["analise"] = "Minha análise",
            ["conclusao"] = "C", ["prazo_validade"] = "6 meses",
        };
        TeamMember[] team = [new("Rita Lima", "Psiquiatria", "CRM 1", "Análise da Rita")];

        // Act
        var sections = DocumentSections.Build("relatorio_multiprofissional", MultiElements, fields, team, "Dra. Paula");

        // Assert
        sections.Select(s => s.Heading).Should().Equal(
            "Descrição da demanda", "Procedimento", "Procedimentos privativos da Psicologia",
            "Análise — Dra. Paula (Psicologia)", "Análise — Rita Lima (Psiquiatria)", "Conclusão", "Prazo de validade");
    }

    [Fact]
    public void Assemble_Laudo_JoinsSectionsAsHeadedBlocks()
    {
        // Arrange
        var fields = new Dictionary<string, string> { ["analise"] = "A", ["prazo_validade"] = "1 ano" };

        // Act
        var body = DocumentBody.Assemble("laudo", LaudoElements, fields, [], "Dra. Paula", "06/12345", "Ana Souza");

        // Assert
        body.Should().Be("1. Identificação\nPessoa atendida: Ana Souza\nAutor(a): Dra. Paula\nCRP: 06/12345"
            + "\n\n2. Análise\nA\n\nPrazo de validade\n1 ano");
    }

    [Fact]
    public void Assemble_Laudo_StartsWithIdentificationIncludingRequesterAndPurpose()
    {
        // Arrange
        var fields = new Dictionary<string, string> { ["nome_solicitante"] = "Juízo", ["finalidade"] = "Instruir processo" };

        // Act
        var body = DocumentBody.Assemble("laudo", LaudoElements, fields, [], "Dra. Paula", "06/12345", "Ana Souza");

        // Assert
        body.Should().Be("1. Identificação\nPessoa atendida: Ana Souza\nAutor(a): Dra. Paula\nCRP: 06/12345"
            + "\nSolicitante: Juízo\nFinalidade: Instruir processo");
    }

    [Fact]
    public void Assemble_Parecer_IdentificationIncludesTitulacao()
    {
        // Arrange
        var fields = new Dictionary<string, string>
        {
            ["nome_autor_inscricao_crp_e_titulacao"] = "Especialista em Psicologia Jurídica", ["analise"] = "A",
        };

        // Act
        var body = DocumentBody.Assemble("parecer", ParecerElements, fields, [], "Dra. Paula", "06/12345", "Ana Souza");

        // Assert
        body.Should().StartWith("1. Identificação\nPessoa objeto do questionamento: Ana Souza\nAutor(a): Dra. Paula\nCRP: 06/12345\nTitulação: Especialista em Psicologia Jurídica\n\n");
    }

    [Fact]
    public void Assemble_Multi_IdentificationListsAuthorshipWithCoAuthorRegistries()
    {
        // Arrange
        var fields = new Dictionary<string, string> { ["descricao_demanda"] = "D" };
        TeamMember[] team = [new("Rita Lima", "Psiquiatria", "CRM 1", "Análise da Rita")];

        // Act
        var body = DocumentBody.Assemble("relatorio_multiprofissional", MultiElements, fields, team, "Dra. Paula", "06/12345", "Ana Souza");

        // Assert
        body.Should().Be("Identificação\nPessoa atendida: Ana Souza\nAutoria: Dra. Paula — Psicologia — CRP 06/12345"
            + "\nRita Lima — Psiquiatria — CRM 1\n\nDescrição da demanda\nD\n\nAnálise — Rita Lima (Psiquiatria)\nAnálise da Rita");
    }
}
