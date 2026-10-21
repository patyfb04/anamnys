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
}
