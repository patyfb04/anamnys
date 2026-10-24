using System.Text;

namespace Anamnys.Server.NoteStructuring;

// ChatML prompt for Qwen2.5 Instruct. The rules restate the CFP limit: organise, never interpret.
public static class NotePrompt
{
    // The prompt is tokenised with special tokens enabled, so dictated text must not be able to
    // close the user turn or open a new one.
    private static readonly string[] ControlStrings = ["<|im_start|>", "<|im_end|>", "<|endoftext|>"];

    internal static string StripControl(string transcript)
    {
        // Repeat until stable: removing one string can join the halves of another.
        while (ControlStrings.Any(c => transcript.Contains(c, StringComparison.Ordinal)))
        {
            foreach (var control in ControlStrings)
            {
                transcript = transcript.Replace(control, "", StringComparison.Ordinal);
            }
        }
        return transcript;
    }

    public static string Build(string transcript, string format, NoteSource source = NoteSource.Dictation)
    {
        var typed = source == NoteSource.Typed;
        var keys = NoteFormats.KeysFor(format);
        var system = new StringBuilder()
            .AppendLine(typed
                ? $"Você organiza as anotações escritas por um(a) psicólogo(a) nas seções de uma evolução clínica no formato {format}."
                : $"Você organiza o ditado de um(a) psicólogo(a) nas seções de uma evolução clínica no formato {format}.")
            .AppendLine("Regras:")
            .AppendLine(typed
                ? "1. Use apenas o que foi escrito. Não acrescente fatos, hipóteses, diagnósticos, interpretações ou conclusões."
                : "1. Use apenas o que foi ditado. Não acrescente fatos, hipóteses, diagnósticos, interpretações ou conclusões.")
            .AppendLine("2. Coloque cada trecho na seção a que pertence:");
        foreach (var key in keys)
        {
            system.AppendLine($"   - \"{key}\": {NoteFormats.Describe(key)}.");
        }
        system
            .AppendLine(typed
                ? "3. Corrija a pontuação; escreva em português, em tom de prontuário, em terceira pessoa."
                : "3. Remova vícios de fala (é, hum, né, tipo), repetições e hesitações; corrija a pontuação; escreva em português, em tom de prontuário, em terceira pessoa.")
            .AppendLine(typed
                ? "4. Preserve nomes, números, doses e datas exatamente como foram escritos. Nunca transforme um número solto em data, horário, dose ou quantidade."
                : "4. Preserve nomes, números, doses e datas exatamente como foram ditados. Nunca transforme um número solto em data, horário, dose ou quantidade.")
            .AppendLine(typed
                ? "5. Se um trecho estiver confuso ou incompleto, copie-o como foi escrito; nunca o reinterprete nem deduza fatos a partir dele."
                : "5. O ditado vem de transcrição automática e pode ter erros. Se um trecho estiver confuso, truncado ou sem sentido, copie-o como foi ditado; nunca o reinterprete nem deduza fatos a partir dele.")
            .AppendLine(typed
                ? "6. Se nada foi escrito para uma seção, deixe-a vazia (\"\")."
                : "6. Se nada foi ditado para uma seção, deixe-a vazia (\"\").")
            .Append("7. Responda somente com o objeto JSON com essas chaves, nessa ordem.");
        var label = typed ? "Anotações" : "Ditado";
        // AppendLine writes \r\n on Windows; ChatML is \n throughout.
        return $"<|im_start|>system\n{system.ToString().ReplaceLineEndings("\n")}<|im_end|>\n<|im_start|>user\n{label}:\n{StripControl(transcript)}<|im_end|>\n<|im_start|>assistant\n";
    }
}
