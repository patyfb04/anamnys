using System.Text;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Documents;
using FluentAssertions;
using QuestPDF.Infrastructure;

namespace Anamnys.Tests.Documents;

public class DocumentPdfTests
{
    private static readonly DateTimeOffset Signed = new(2026, 10, 19, 14, 30, 0, TimeSpan.Zero);
    private static readonly Guid DocumentId = Guid.Parse("a1b2c3d4-0000-0000-0000-000000000000");

    static DocumentPdfTests() => QuestPDF.Settings.License = LicenseType.Community;

    // Element lists copied from the seeded rulesets (design/migrations/2026-10-19-documents.sql).
    private static readonly string[] DeclaracaoElements =
    [
        "titulo_declaracao", "nome_pessoa_atendida", "finalidade", "local_dias_horarios_e_duracao_do_acompanhamento",
        "local_emissao", "data_emissao", "carimbo", "nome_psicologo", "inscricao_crp", "assinatura",
        "laudas_numeradas_rubricadas", "prazo_validade",
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

    private static Dictionary<string, string> FilledFields(string kind, string[] elements) =>
        DocumentBody.ProviderFieldOrder(kind, elements).ToDictionary(code => code, code => $"Texto de {code}.");

    private static DocumentPdfModel ModelFor(
        string kind, string[] elements, Dictionary<string, string> fields, List<TeamMember>? team = null)
    {
        var document = new ClinicalDocument
        {
            Id = DocumentId, Kind = kind, Fields = fields, SignedAt = Signed, SignedByCrp = "06/12345",
            Team = team ?? [],
            Body = DocumentBody.Assemble(kind, elements, fields, team ?? [], "Dra. Paula Souza", "06/12345", "Ana Souza"),
        };
        return DocumentPdfModel.From(
            document, new ConformityRuleset { DocumentKind = kind, Elements = [.. elements] },
            new Patient { FirstName = "Ana", LastName = "Souza" },
            new Provider { Name = "Dra. Paula Souza", PracticeCity = "São Paulo" });
    }

    [Fact]
    public void From_Declaracao_LabelledParagraphsWithoutHeadingsAndEveryProviderElement()
    {
        // Arrange
        var fields = FilledFields("declaracao", DeclaracaoElements);

        // Act
        var model = ModelFor("declaracao", DeclaracaoElements, fields);

        // Assert
        model.Title.Should().Be("Declaração");
        model.Sections.Should().OnlyContain(s => s.Heading == null);
        model.Sections.Should().HaveCount(
            DocumentBody.ProviderFieldOrder("declaracao", DeclaracaoElements).Count);
        model.Sections.Should().OnlyContain(s => !s.Text.Contains("\n\n"));
        model.Sections[^1].Text.Should().StartWith("Prazo de validade:");
        AssertEveryProviderLabelPresent("declaracao", DeclaracaoElements, string.Join(" ", model.Sections.Select(s => s.Text)));
        model.IdentificationHeading.Should().BeNull();
        model.Identification.Select(l => l.Label).Should().Equal("Paciente");
    }

    [Fact]
    public void From_Declaracao_KeepsLineBreaksTypedInsideAValue()
    {
        // Arrange
        var fields = FilledFields("declaracao", DeclaracaoElements);
        fields["finalidade"] = "Primeira linha.\nSegunda linha.";

        // Act
        var model = ModelFor("declaracao", DeclaracaoElements, fields);

        // Assert
        model.Sections.Should().Contain(s => s.Text == "Finalidade: Primeira linha.\nSegunda linha.");
    }

    [Fact]
    public void From_Atestado_SingleParagraphWithoutLineBreaksAndEveryProviderElement()
    {
        // Arrange
        var fields = FilledFields("atestado", AtestadoElements);
        fields["descricao_condicoes_psicologicas"] = "Texto de descricao_condicoes_psicologicas.\nSegunda linha.";

        // Act
        var model = ModelFor("atestado", AtestadoElements, fields);

        // Assert
        var section = model.Sections.Should().ContainSingle().Subject;
        section.Heading.Should().BeNull();
        section.Text.Should().NotContain("\n").And.EndWith("Texto de prazo_validade.");
        AssertEveryProviderLabelPresent("atestado", AtestadoElements, section.Text);
    }

    [Fact]
    public void From_Relatorio_HeadingIsLabelOfEveryProviderElementInOrderPrazoLastAndAuthorShown()
    {
        // Arrange
        var fields = FilledFields("relatorio", RelatorioElements);

        // Act
        var model = ModelFor("relatorio", RelatorioElements, fields);

        // Assert
        var expected = DocumentBody.ProviderFieldOrder("relatorio", RelatorioElements)
            .Select(code => DocumentCatalog.Element("relatorio", code).Label).ToList();
        model.Sections.Select(s => s.Heading).Should().Equal(expected);
        expected.Last().Should().Be("Prazo de validade");
        model.Sections.Select(s => s.Text).Should().Equal(
            DocumentBody.ProviderFieldOrder("relatorio", RelatorioElements).Select(code => $"Texto de {code}."));
        model.IdentificationHeading.Should().BeNull();
        model.Identification.Select(l => l.Label).Should().Equal("Paciente", "Autor(a)", "CRP");
        model.Identification.Should().Contain(new DocumentPdfLine("Autor(a)", "Dra. Paula Souza"));
        model.Crp.Should().Be("06/12345");
    }

    [Fact]
    public void From_Document_CarriesCodePatientCityAndSigningInfo()
    {
        // Arrange
        var fields = FilledFields("declaracao", DeclaracaoElements);

        // Act
        var model = ModelFor("declaracao", DeclaracaoElements, fields);

        // Assert
        model.Code.Should().Be("A1B2C3D4");
        model.FileName.Should().Be("documento-A1B2C3D4.pdf");
        model.PatientName.Should().Be("Ana Souza");
        model.PracticeCity.Should().Be("São Paulo");
        model.SignedAt.Should().Be(Signed);
        DocumentPdfModel.FileNameFor(DocumentId).Should().Be("documento-A1B2C3D4.pdf");
    }

    [Fact]
    public void Render_EachKindWithEveryProviderElementFilled_ProducesPdfBytes()
    {
        // Arrange
        var models = new[]
        {
            ModelFor("declaracao", DeclaracaoElements, FilledFields("declaracao", DeclaracaoElements)),
            ModelFor("atestado", AtestadoElements, FilledFields("atestado", AtestadoElements)),
            ModelFor("relatorio", RelatorioElements, FilledFields("relatorio", RelatorioElements)),
        };

        // Act
        var rendered = models.Select(DocumentPdf.Render).ToList();

        // Assert
        rendered.Should().OnlyContain(bytes => bytes.Length > 0 && Encoding.ASCII.GetString(bytes, 0, 4) == "%PDF");
    }

    [Fact]
    public void From_Laudo_NumberedIdentificationLinesAndSections()
    {
        // Arrange
        var fields = FilledFields("laudo", DocumentConformityTests.LaudoElements);

        // Act
        var model = ModelFor("laudo", DocumentConformityTests.LaudoElements, fields);

        // Assert
        model.IdentificationHeading.Should().Be("1. Identificação");
        model.Identification.Select(l => l.Label).Should().Equal(
            "Pessoa atendida", "Autor(a)", "CRP", "Solicitante", "Finalidade");
        model.Sections.Should().NotBeEmpty();
        DocumentPdf.Render(model).Take(4).Should().Equal("%PDF"u8.ToArray());
    }

    [Fact]
    public void From_Laudo_SectionHeadingsFollowTheResolutionOrder()
    {
        // Arrange
        var fields = FilledFields("laudo", DocumentConformityTests.LaudoElements);

        // Act
        var model = ModelFor("laudo", DocumentConformityTests.LaudoElements, fields);

        // Assert
        model.Sections.Select(s => s.Heading).Should().Equal(
            "2. Descrição da demanda", "3. Procedimento", "Referencial teórico-metodológico",
            "Pessoas ouvidas, número de encontros e duração", "4. Análise", "5. Conclusão", "6. Referências",
            "Prazo de validade");
    }

    [Fact]
    public void From_Parecer_AddsTitulacaoLine()
    {
        // Arrange
        var fields = FilledFields("parecer", DocumentConformityTests.ParecerElements);

        // Act
        var model = ModelFor("parecer", DocumentConformityTests.ParecerElements, fields);

        // Assert
        model.IdentificationHeading.Should().Be("1. Identificação");
        model.Identification.Select(l => l.Label).Should().Equal(
            "Pessoa objeto do questionamento", "Autor(a)", "CRP", "Titulação", "Solicitante", "Finalidade");
        DocumentPdf.Render(model).Take(4).Should().Equal("%PDF"u8.ToArray());
    }

    [Fact]
    public void From_Multiprofissional_ListsAuthorshipOfEveryProfessional()
    {
        // Arrange
        var fields = FilledFields("relatorio_multiprofissional", DocumentConformityTests.MultiElements);
        List<TeamMember> team = [new("Rita Lima", "Psiquiatria", "CRM 1", "Análise da Rita")];

        // Act
        var model = ModelFor("relatorio_multiprofissional", DocumentConformityTests.MultiElements, fields, team);

        // Assert
        model.IdentificationHeading.Should().Be("Identificação");
        model.Identification.Should().Contain(new DocumentPdfLine(
            "Autoria", "Dra. Paula Souza — Psicologia — CRP 06/12345\nRita Lima — Psiquiatria — CRM 1"));
        model.Sections.Select(s => s.Heading).Should().Contain("Análise — Rita Lima (Psiquiatria)");
        DocumentPdf.Render(model).Take(4).Should().Equal("%PDF"u8.ToArray());
    }

    private static void AssertEveryProviderLabelPresent(string kind, string[] elements, string text)
    {
        foreach (var code in DocumentBody.ProviderFieldOrder(kind, elements))
        {
            text.Should().Contain($"{DocumentCatalog.Element(kind, code).Label}: Texto de {code}");
        }
    }
}
