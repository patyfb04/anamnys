using System.Text;

namespace Anamnys.Server.NoteStructuring;

// ChatML prompt for Qwen2.5 Instruct. The rules restate the CFP limit: organise, never interpret.
public static class NotePrompt
{
    // The prompt is tokenised with special tokens enabled, so dictated text must not be able to
    // close the user turn or open a new one.
    private static readonly string[] ControlStrings = ["<|im_start|>", "<|im_end|>", "<|endoftext|>"];

    private static string StripControl(string transcript)
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

    public static string Build(string transcript, string format)
    {
        var keys = NoteFormats.KeysFor(format);
        var system = new StringBuilder()
            .AppendLine($"Você organiza o ditado de um(a) psicólogo(a) nas seções de uma evolução clínica no formato {format}.")
            .AppendLine("Regras:")
            .AppendLine("1. Use apenas o que foi ditado. Não acrescente fatos, hipóteses, diagnósticos, interpretações ou conclusões.")
            .AppendLine("2. Coloque cada trecho na seção a que pertence:");
        foreach (var key in keys)
        {
            system.AppendLine($"   - \"{key}\": {NoteFormats.Describe(key)}.");
        }
        system
            .AppendLine("3. Remova vícios de fala (é, hum, né, tipo), repetições e hesitações; corrija a pontuação; escreva em português, em tom de prontuário, em terceira pessoa.")
            .AppendLine("4. Preserve nomes, números, doses e datas exatamente como foram ditados.")
            .AppendLine("5. Se nada foi ditado para uma seção, deixe-a vazia (\"\").")
            .Append("6. Responda somente com o objeto JSON com essas chaves, nessa ordem.");
        // AppendLine writes \r\n on Windows; ChatML is \n throughout.
        return $"<|im_start|>system\n{system.ToString().ReplaceLineEndings("\n")}<|im_end|>\n<|im_start|>user\nDitado:\n{StripControl(transcript)}<|im_end|>\n<|im_start|>assistant\n";
    }
}
