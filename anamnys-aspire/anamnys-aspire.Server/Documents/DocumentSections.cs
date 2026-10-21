using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Documents;

// Content sections of the item-based kinds (Res. CFP 006/2019 arts. 12–14), shared by the signed
// Body and the PDF. Laudo and parecer number their items; item 1 is the identification block the
// PDF prints before these sections. Empty fields are skipped and numbering stays consecutive.
public static class DocumentSections
{
    public static readonly string[] ItemKinds = ["laudo", "parecer", "relatorio_multiprofissional"];

    private sealed record Item(string Code, bool Numbered);

    private static readonly Item[] Laudo =
    [
        new("descricao_demanda", true), new("procedimento", true), new("referencial_teorico_metodologico", false),
        new("pessoas_ouvidas_numero_de_encontros_e_duracao", false), new("analise", true), new("conclusao", true),
        new("referencias", true),
    ];

    private static readonly Item[] Parecer =
    [
        new("descricao_demanda", true), new("analise", true), new("conclusao", true), new("referencias", true),
    ];

    private static readonly Item[] Multi =
    [
        new("descricao_demanda", false), new("procedimento", false),
        new("procedimentos_privativos_da_psicologia_em_secao_separada", false),
    ];

    public static IReadOnlyList<DocumentPdfSection> Build(
        string kind, IReadOnlyList<string> elements, IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<TeamMember> team, string authorName)
    {
        var sections = new List<DocumentPdfSection>();
        var number = 1;
        void Add(Item item)
        {
            var text = Value(fields, item.Code);
            if (!elements.Contains(item.Code) || (text.Length == 0 && !HoldsLaudoSubItems(kind, item.Code)))
            {
                return;
            }
            var label = DocumentCatalog.Element(kind, item.Code).Label;
            sections.Add(new DocumentPdfSection(item.Numbered ? $"{++number}. {label}" : label, text));
        }

        if (kind == "relatorio_multiprofissional")
        {
            foreach (var item in Multi)
            {
                Add(item);
            }
            var own = Value(fields, "analise");
            if (own.Length > 0)
            {
                sections.Add(new DocumentPdfSection($"Análise — {authorName} (Psicologia)", own));
            }
            foreach (var member in team.Where(m => !string.IsNullOrWhiteSpace(m.Analysis)))
            {
                sections.Add(new DocumentPdfSection($"Análise — {member.Name.Trim()} ({member.Category.Trim()})", member.Analysis.Trim()));
            }
            Add(new Item("conclusao", false));
        }
        else
        {
            foreach (var item in kind == "laudo" ? Laudo : Parecer)
            {
                Add(item);
            }
        }

        var validity = Value(fields, "prazo_validade");
        if (validity.Length > 0)
        {
            sections.Add(new DocumentPdfSection(DocumentCatalog.Element(kind, "prazo_validade").Label, validity));
        }
        return sections;

        // The laudo's unnumbered sub-items belong under "Procedimento": its heading prints when any has text.
        bool HoldsLaudoSubItems(string k, string code) =>
            k == "laudo" && code == "procedimento" &&
            Laudo.Where(i => !i.Numbered).Any(i => elements.Contains(i.Code) && Value(fields, i.Code).Length > 0);
    }

    public static (string? Heading, IReadOnlyList<DocumentPdfLine> Lines) Identification(
        string kind, IReadOnlyDictionary<string, string> fields, IReadOnlyList<TeamMember> team,
        string name, string crp, string patient)
    {
        string Field(string code) => Value(fields, code);
        return kind switch
        {
            "relatorio" => (null, [new("Paciente", patient), new("Autor(a)", name), new("CRP", crp)]),
            "laudo" => ("1. Identificação",
            [
                new("Pessoa atendida", patient), new("Autor(a)", name), new("CRP", crp),
                new("Solicitante", Field("nome_solicitante")), new("Finalidade", Field("finalidade")),
            ]),
            "parecer" => ("1. Identificação",
            [
                new("Pessoa objeto do questionamento", patient), new("Autor(a)", name), new("CRP", crp),
                new("Titulação", Field("nome_autor_inscricao_crp_e_titulacao")),
                new("Solicitante", Field("nome_solicitante")), new("Finalidade", Field("finalidade")),
            ]),
            "relatorio_multiprofissional" => ("Identificação",
            [
                new("Pessoa atendida", patient), new("Solicitante", Field("nome_solicitante")),
                new("Finalidade", Field("finalidade")),
                new("Autoria", string.Join("\n",
                [
                    $"{name} — Psicologia — CRP {crp}",
                    .. team.Select(m => $"{m.Name.Trim()} — {m.Category.Trim()} — {m.Registry.Trim()}"),
                ])),
            ]),
            _ => (null, [new("Paciente", patient)]),
        };
    }

    private static string Value(IReadOnlyDictionary<string, string> fields, string code) =>
        fields.TryGetValue(code, out var value) ? value.Trim() : "";
}
