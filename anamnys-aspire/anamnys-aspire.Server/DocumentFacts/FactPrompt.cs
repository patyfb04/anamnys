using System.Globalization;
using Anamnys.Server.NoteStructuring;

namespace Anamnys.Server.DocumentFacts;

// ChatML prompt for Qwen2.5 Instruct (design/specs/2026-10-26-document-facts-design.md §3): only what the
// document states; never infer; copy the sentence verbatim; dates as written. The constant part (rules)
// comes first, then the pages, each headed by its number, then the instruction and the assistant turn.
public static class FactPrompt
{
    public const string Suffix = "<|im_end|>\n<|im_start|>assistant\n";

    public const string Instruction =
        "\n\nResponda com o array JSON de objetos {\"kind\", \"page\", \"quote\", \"value\"}.";

    public static string Prefix()
    {
        const string system =
            "Você lê documentos clínicos recebidos de outros profissionais (laudos, receitas, exames, encaminhamentos, " +
            "relatórios) e lista os fatos que o documento declara.\n" +
            "Tipos (kind):\n" +
            "- issuer: quem emitiu o documento (profissional ou instituição), como escrito.\n" +
            "- registration: registro do emissor no conselho profissional (sigla e número), ex.: CRM-SP 123456, CRP 06/12345.\n" +
            "- issue_date: data em que o documento foi emitido, como escrita.\n" +
            "- icd: código CID-10 citado no documento, só o código, ex.: F32.1; todo código CID citado é um icd, " +
            "além da conclusão que o acompanha.\n" +
            "- medication: medicamento prescrito ou em uso, com dose e posologia, como escrito; um objeto por " +
            "medicamento; resultado de exame não é medicamento.\n" +
            "- conclusion: conclusão, diagnóstico, hipótese ou resultado de exame declarado pelo emissor.\n" +
            "Regras: só o que o documento afirma; nunca deduza, complete ou corrija; dados do paciente (nome, nascimento, " +
            "endereço) não são fatos; page é o número da página onde está o fato; quote copia dessa página, sem mudar " +
            "nenhuma palavra, a frase ou linha onde o fato aparece (até 200 caracteres); value é o fato, curto, como " +
            "está no documento; datas como escritas; não repita fatos; no máximo 12; nenhum: [].";
        return $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\nDocumento:\n";
    }

    // One page as given to the model: its number, then its text (control strings stripped).
    public static string Page(int pageNumber, string text) =>
        $"[Página {pageNumber.ToString(CultureInfo.InvariantCulture)}]\n{NotePrompt.StripControl(text).Trim()}\n";

    public static string Build(IEnumerable<FactPage> pages) =>
        Prefix() + string.Concat(pages.Select(p => Page(p.PageNumber, p.Text))) + Instruction + Suffix;
}
