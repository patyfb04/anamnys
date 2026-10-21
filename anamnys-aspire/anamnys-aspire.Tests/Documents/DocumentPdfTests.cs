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

    private static DocumentPdfModel ModelFor(string kind, string[] elements, Dictionary<string, string> fields)
    {
        var document = new ClinicalDocument
        {
            Id = DocumentId, Kind = kind, Fields = fields, SignedAt = Signed, SignedByCrp = "06/12345",
            Body = DocumentBody.Assemble(kind, elements, fields),
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
        model.Author.Should().BeNull();
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
        model.Author.Should().Be("Dra. Paula Souza");
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

    private static void AssertEveryProviderLabelPresent(string kind, string[] elements, string text)
    {
        foreach (var code in DocumentBody.ProviderFieldOrder(kind, elements))
        {
            text.Should().Contain($"{DocumentCatalog.Element(kind, code).Label}: Texto de {code}");
        }
    }
}
