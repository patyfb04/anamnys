namespace Anamnys.Server.Documents;

public enum ElementOrigin { System, Pdf, Provider }

public sealed record ElementInfo(string Code, ElementOrigin Origin, string Label, string? Hint);

public sealed record ProhibitionInfo(string Code, bool ByConstruction, string? ConfirmationText);

// Origins, pt-BR labels and hints for the elements of the Res. CFP 006/2019 rulesets
// (design/specs/2026-10-19-psychological-documents-design.md, section 3).
// Hints cite only references present in the seeded ruleset's ArticleRef: the kind's article,
// Art. 17 (prazo_validade) and Art. 5º §8º (laudas_numeradas_rubricadas).
public static class DocumentCatalog
{
    public static readonly string[] AvailableKinds = ["declaracao", "atestado", "relatorio", "relatorio_multiprofissional", "laudo", "parecer"];

    private static readonly Dictionary<string, string> KindTitles = new()
    {
        ["declaracao"] = "Declaração",
        ["atestado"] = "Atestado psicológico",
        ["relatorio"] = "Relatório psicológico",
        ["relatorio_multiprofissional"] = "Relatório multiprofissional",
        ["laudo"] = "Laudo psicológico",
        ["parecer"] = "Parecer psicológico",
    };

    private static readonly Dictionary<string, string> KindArticles = new()
    {
        ["declaracao"] = "Art. 9º",
        ["atestado"] = "Art. 10",
        ["relatorio"] = "Art. 11",
        ["relatorio_multiprofissional"] = "Art. 12",
        ["laudo"] = "Art. 13",
        ["parecer"] = "Art. 14",
    };

    private sealed record Entry(ElementOrigin Origin, string Label, string Explanation);

    private static Entry Sys(string label, string explanation) => new(ElementOrigin.System, label, explanation);
    private static Entry Pdf(string label, string explanation) => new(ElementOrigin.Pdf, label, explanation);
    private static Entry Prov(string label, string explanation) => new(ElementOrigin.Provider, label, explanation);

    private const string TitleText = "O título é impresso pelo PDF.";
    private const string PatientNameText = "Preenchido com o nome completo do paciente.";
    private const string RequesterText = "Quem solicitou o documento (pessoa ou instituição).";

    private static readonly Dictionary<string, Entry> Entries = new()
    {
        ["titulo_declaracao"] = Pdf("Título (Declaração)", TitleText),
        ["titulo_atestado_psicologico"] = Pdf("Título (Atestado psicológico)", TitleText),
        ["titulo_relatorio_psicologico"] = Pdf("Título (Relatório psicológico)", TitleText),
        ["titulo_relatorio_multiprofissional"] = Pdf("Título (Relatório multiprofissional)", TitleText),
        ["titulo_laudo_psicologico"] = Pdf("Título (Laudo psicológico)", TitleText),
        ["titulo_parecer_psicologico"] = Pdf("Título (Parecer psicológico)", TitleText),
        ["nome_pessoa_atendida"] = Sys("Nome da pessoa atendida", PatientNameText),
        ["nome_pessoa_ou_instituicao_atendida"] = Sys("Nome da pessoa ou instituição atendida", PatientNameText),
        ["identificacao"] = Sys("Identificação", "Bloco com paciente, autor e CRP, impresso pelo PDF."),
        ["nome_psicologo"] = Sys("Nome da(o) psicóloga(o)", "Depende do CRP no perfil."),
        ["inscricao_crp"] = Sys("Inscrição no CRP", "Depende do CRP no perfil."),
        ["nome_autor_e_inscricao_crp"] = Sys("Nome da(o) autora(or) e inscrição no CRP", "Depende do CRP no perfil."),
        ["local_emissao"] = Sys("Local de emissão", "Preenchido com a Cidade de atendimento do perfil."),
        ["data_emissao"] = Sys("Data de emissão", "Preenchida com a data da assinatura."),
        ["carimbo"] = Pdf("Carimbo", "Bloco com nome e CRP ao lado da assinatura eletrônica."),
        ["assinatura"] = Pdf("Assinatura", "Assinatura eletrônica na última página."),
        ["laudas_numeradas_rubricadas"] = Pdf("Laudas numeradas e rubricadas", "O rodapé traz Página X de Y, nome e CRP."),
        ["prazo_validade"] = Prov("Prazo de validade", "Indique o prazo de validade do conteúdo no último parágrafo do documento."),

        ["finalidade"] = Prov("Finalidade", "A finalidade para a qual o documento é emitido."),
        ["local_dias_horarios_e_duracao_do_acompanhamento"] = Prov("Local, dias, horários e duração do acompanhamento",
            "Registre apenas a prestação do serviço, sem descrever estado psicológico."),

        ["nome_solicitante"] = Prov("Nome do solicitante", RequesterText),
        ["descricao_condicoes_psicologicas"] = Prov("Descrição das condições psicológicas",
            "Texto corrido, restrito à informação solicitada, sem quebras de parágrafo."),

        ["descricao_demanda"] = Prov("Descrição da demanda", "O que motivou o pedido e quem o fez."),
        ["procedimento"] = Prov("Procedimento", "Recursos e instrumentos usados."),
        ["referencial_teorico"] = Prov("Referencial teórico", "Abordagem teórica que fundamenta o trabalho."),
        ["pessoas_ouvidas_numero_de_encontros_e_duracao"] = Prov("Pessoas ouvidas, número de encontros e duração",
            "Quem foi ouvido, em quantos encontros e por quanto tempo."),
        ["analise"] = Prov("Análise", "Análise fundamentada, sem afirmação sem fonte nem transcrição literal das sessões."),
        ["conclusao"] = Prov("Conclusão", "Conclusões a que se chegou, de acordo com a análise."),

        ["nome_pessoa_ou_instituicao_objeto_do_questionamento"] = Sys("Pessoa objeto do questionamento", PatientNameText),
        ["nome_autor_inscricao_crp_e_titulacao"] = Prov("Titulação da(o) autora(or)",
            "Titulação que comprove conhecimento específico no assunto; nome e CRP vêm do perfil."),
        ["nomes_autores_categorias_e_registros_profissionais"] = Sys("Autoras(es), categorias e registros profissionais",
            "Você e a equipe informada no documento, com categoria e registro."),
        ["procedimentos_privativos_da_psicologia_em_secao_separada"] = Prov("Procedimentos privativos da Psicologia",
            "Procedimentos exclusivos da Psicologia, em seção separada."),
        ["analise_identificada_por_profissional_e_categoria"] = Sys("Análise por profissional e categoria",
            "Cada profissional da equipe tem sua análise, identificada por nome e categoria."),
        ["nomes_profissionais"] = Sys("Nomes das(os) profissionais", "Você e a equipe informada no documento."),
        ["inscricoes_profissionais"] = Sys("Inscrições profissionais", "Seu CRP e os registros da equipe."),
        ["referencias"] = Prov("Referências", "Referências obrigatórias, uma por linha."),
        ["referencial_teorico_metodologico"] = Prov("Referencial teórico-metodológico", "Impresso dentro do item Procedimento."),
        ["secoes_em_forma_de_itens"] = Pdf("Seções em forma de itens", "O PDF organiza o documento em itens numerados."),
        ["assinatura_do_psicologo"] = Pdf("Assinatura da(o) psicóloga(o)", "Assinatura eletrônica na última página."),
    };

    private static readonly Dictionary<string, ProhibitionInfo> Prohibitions = new[]
    {
        new ProhibitionInfo("sintomas_situacoes_ou_estados_psicologicos", false,
            "Confirmo que esta declaração não descreve sintomas, situações ou estados psicológicos."),
        new ProhibitionInfo("quebra_de_paragrafo_sem_preenchimento", true, null),
        new ProhibitionInfo("informacao_alem_do_solicitado", false,
            "Confirmo que o atestado se restringe à informação solicitada."),
        new ProhibitionInfo("resultado_de_avaliacao_ou_intervencao_psicologica", false,
            "Confirmo que este parecer não resulta de processo de avaliação ou intervenção psicológica."),
    }.ToDictionary(p => p.Code);

    private static readonly Dictionary<string, string> ReportNouns = new()
    {
        ["relatorio"] = "relatório",
        ["laudo"] = "laudo",
        ["relatorio_multiprofissional"] = "relatório multiprofissional",
    };

    public static string KindTitle(string kind) =>
        KindTitles.TryGetValue(kind, out var title) ? title : kind;

    // Unknown codes are treated as provider-written so the check can never silently pass them.
    public static ElementInfo Element(string kind, string code)
    {
        if (!Entries.TryGetValue(code, out var entry))
            return new ElementInfo(code, ElementOrigin.Provider, code, null);

        return new ElementInfo(code, entry.Origin, entry.Label, $"{entry.Explanation} ({Article(kind, code)})");
    }

    private static string Article(string kind, string code) => code switch
    {
        "prazo_validade" => "Art. 17",
        "laudas_numeradas_rubricadas" => "Art. 5º §8º",
        _ => KindArticles.GetValueOrDefault(kind, "Res. CFP 006/2019"),
    };

    // Unknown codes require an explicit confirmation without text.
    public static ProhibitionInfo Prohibition(string kind, string code)
    {
        var noun = ReportNouns.GetValueOrDefault(kind, "relatório");
        return code switch
        {
            "afirmacao_sem_fonte_ou_sustentacao" => new(code, false, $"Confirmo que toda afirmação do {noun} tem fonte ou sustentação técnica."),
            "descricao_literal_das_sessoes" => new(code, false, $"Confirmo que o {noun} não transcreve literalmente as sessões."),
            _ => Prohibitions.TryGetValue(code, out var info) ? info : new ProhibitionInfo(code, false, null),
        };
    }
}
