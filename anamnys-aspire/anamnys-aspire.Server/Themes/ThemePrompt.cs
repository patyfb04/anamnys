using System.Security.Cryptography;
using System.Text;
using Anamnys.Server.NoteStructuring;

namespace Anamnys.Server.Themes;

// ChatML prompt for Qwen2.5 Instruct (design/specs/2026-10-21-theme-vocabulary-design.md §4):
// choose only what the text explicitly talks about; never infer a state, risk or diagnosis.
// Laid out for a prefix cache: everything that depends only on the vocabulary (rules + the full
// active term list) comes first and is identical for every note, so its KV state is evaluated once
// and reused; the note text and the assistant turn come last. Exclusions (terms already tagged,
// terms the provider removed) never enter the prompt: they are dropped from the parsed output
// (ThemeGrammar.Pick), so neither the prefix nor the grammar's code list depends on the note.
// Two output modes share that prefix (F-03): codes only (the on-demand suggest dialog, fast) and
// {code, quote} items (structuring proposals and the background locate, which uses a grammar that
// fixes the codes, ThemeGrammar.ForQuotes). The mode's instruction follows the note text.
public static class ThemePrompt
{
    // Closes the user turn and opens the assistant turn; evaluated per request after the text.
    public const string Suffix = "<|im_end|>\n<|im_start|>assistant\n";

    private static readonly Dictionary<string, string> SectionTitles = new(StringComparer.Ordinal)
    {
        ["data"] = "Dados", ["assessment"] = "Avaliação", ["plan"] = "Plano",
        ["subjective"] = "Subjetivo", ["objective"] = "Objetivo",
        ["biological"] = "Biológico", ["psychological"] = "Psicológico", ["social"] = "Social",
    };

    // The note's sections as "Título: conteúdo" (non-empty only, in order) or, without sections,
    // the raw transcript / typed text.
    public static string NoteText(IReadOnlyList<(string Key, string Content)> sections, string? rawTranscript)
    {
        var lines = sections
            .Where(s => !string.IsNullOrWhiteSpace(s.Content))
            .Select(s => $"{SectionTitles.GetValueOrDefault(s.Key, s.Key)}: {s.Content.Trim()}")
            .ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : rawTranscript?.Trim() ?? "";
    }

    // The whole prompt: Prefix(terms) + Body(text).
    public static string Build(string text, IReadOnlyList<ThemeTermOption> terms, bool withQuotes = false) =>
        Prefix(terms) + Body(text, withQuotes);

    // The constant part: system rules, the full term list, and the opening of the user turn. Depends
    // only on the terms (codes, labels, parents), never on the note.
    public static string Prefix(IReadOnlyList<ThemeTermOption> terms)
    {
        var system = new StringBuilder()
            .Append("Marque a evolução clínica de psicologia com temas da lista.\n")
            .Append("Regras: só temas de que o texto fala explicitamente; nunca deduza estado, risco ou diagnóstico; ")
            .Append($"prefira o subtema (pai > filho) mais específico; no máximo {ThemeGrammar.MaxItems}; nenhum: []. ")
            .Append("Responda só com o array JSON pedido no fim da mensagem.\n")
            .Append("Temas (código: nome):");
        foreach (var term in Ordered(terms))
        {
            var name = term.ParentLabel is { } parent ? $"{parent} > {term.Label}" : term.Label;
            system.Append($"\n- {term.Code}: {NotePrompt.StripControl(name)}");
        }
        return $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\nEvolução:\n";
    }

    // What to answer, per mode. After the note, not in the prefix, so both modes share the cached prefix.
    public const string CodesInstruction = "\n\nResponda com o array JSON dos códigos.";
    public const string QuotesInstruction =
        "\n\nResponda com o array JSON de objetos {\"code\": código, \"quote\": trecho}, em que trecho copia do texto, " +
        "sem mudar nenhuma palavra, as poucas palavras (até 6) que falam do tema.";

    public static string Instruction(bool withQuotes) => withQuotes ? QuotesInstruction : CodesInstruction;

    // The per-request part: the note text (control strings stripped), the mode's instruction and the
    // assistant turn.
    public static string Body(string text, bool withQuotes = false) => NotePrompt.StripControl(text) + Instruction(withQuotes) + Suffix;

    // Cache key of a prefix: changes whenever a code, label, parent or the rules change.
    public static string PrefixKey(string prefix) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix)));

    // Cuts the text to at most maxTokens tokens: themes from a prefix are still useful, so a long
    // note never fails for length. countTokens is the model's tokenizer.
    public static string Fit(string text, int maxTokens, Func<string, int> countTokens)
    {
        var count = countTokens(text);
        if (count <= maxTokens)
        {
            return text;
        }
        var length = maxTokens <= 0 ? 0 : (int)((long)text.Length * maxTokens / count);
        while (length > 0)
        {
            if (char.IsHighSurrogate(text[length - 1]))
            {
                length--;
                continue;
            }
            var cut = text[..length];
            if (countTokens(cut) <= maxTokens)
            {
                return cut;
            }
            length = length * 9 / 10;
        }
        return "";
    }

    // Each top-level term followed by its children.
    private static IEnumerable<ThemeTermOption> Ordered(IReadOnlyList<ThemeTermOption> terms) =>
        terms
            .OrderBy(t => t.ParentLabel ?? t.Label, StringComparer.CurrentCulture)
            .ThenBy(t => t.ParentLabel is null ? 0 : 1)
            .ThenBy(t => t.Label, StringComparer.CurrentCulture)
            .ThenBy(t => t.Code, StringComparer.Ordinal);
}
