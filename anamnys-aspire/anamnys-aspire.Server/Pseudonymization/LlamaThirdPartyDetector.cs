using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Anamnys.Server.NoteStructuring;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.Pseudonymization;

// Detects third parties with the process's Qwen weights (LlmModelProvider: the same model instance as structuring) under
// the shared LlmGate (design/specs/2026-11-17-pseudonymization-design.md §3). The text is cut into runs of at most
// TextTokensPerRun tokens at sentence ends; each run gets a fixed Portuguese prompt and a GBNF grammar that only admits
// {"entities":[{"text":"…","kind":"person|organization|school|address"},…]}. The returned strings are located in the
// whole text by exact match (every occurrence, on word boundaries); a string not found verbatim is ignored.
// Nothing here logs, and no exception carries the text or the model output.
public sealed partial class LlamaThirdPartyDetector(
    Func<CancellationToken, Task<(LLamaWeights Weights, ModelParams Params)>> load, NoteStructuringOptions options, LlmGate gate,
    LlmPriority priority = LlmPriority.Interactive) : IThirdPartyDetector
{
    internal const int TextTokensPerRun = 1536;
    internal const int OutputTokens = 1024;
    // A run whose output was cut at OutputTokens is split in two and re-run, at most this many times deep.
    internal const int MaxSplitDepth = 3;
    private const int Margin = 64;
    private const int ContextStep = 256;

    // Budget of one model run, counted from when the gate is held (waiting for the gate never counts). A slower run is a
    // TimeoutException, i.e. a failed step; a long transcript is many runs, each with its own budget.
    // NoteStructuring:PseudonymizationRunTimeout (default 12 minutes); a non-positive value falls back to the default.
    public TimeSpan RunTimeout { get; init; } = options.PseudonymizationRunTimeout > TimeSpan.Zero
        ? options.PseudonymizationRunTimeout
        : new NoteStructuringOptions().PseudonymizationRunTimeout;

    // The gate priority of every model run of this instance.
    public LlmPriority Priority => priority;

    // Live work (a provider waits for the note) runs at Interactive; a background backfill builds its own instance at
    // Background over the same model provider and gate.
    public LlamaThirdPartyDetector(LlmModelProvider models, IOptions<NoteStructuringOptions> options, LlmGate gate)
        : this(models.GetAsync, options.Value, gate) { }

    public async Task<IReadOnlyList<DetectedSpan>> DetectAsync(string text, IReadOnlyCollection<string> exclusions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var (weights, parameters) = await load(ct);
        int Count(string s) => weights.Tokenize(s, false, false, Encoding.UTF8).Length;

        Task<(string Output, bool Truncated)> Run(string chunk, CancellationToken token) =>
            GatedAsync(gate, priority, RunTimeout, TimeProvider.System, runToken =>
            {
                var prompt = Prompt(chunk, exclusions);
                var promptTokens = weights.Tokenize(prompt, true, true, Encoding.UTF8).Length;
                return RunAsync(weights, parameters, prompt, promptTokens, runToken);
            }, token);

        var found = new List<(string Text, string Kind)>();
        foreach (var chunk in Chunks(text, TextTokensPerRun, Count))
        {
            found.AddRange(await DetectChunkAsync(chunk, Run, ct));
        }
        return Locate(text, found, exclusions);
    }

    // One chunk: parsed output, or, when the output was cut off by the token budget, the two halves of the chunk run apart.
    internal static async Task<IReadOnlyList<(string Text, string Kind)>> DetectChunkAsync(
        string chunk, Func<string, CancellationToken, Task<(string Output, bool Truncated)>> run, CancellationToken ct, int depth = 0)
    {
        var (output, truncated) = await run(chunk, ct);
        try
        {
            return Parse(output);
        }
        catch (FormatException) when (truncated && depth < MaxSplitDepth && SplitInHalf(chunk) is { } halves)
        {
            var first = await DetectChunkAsync(halves.First, run, ct, depth + 1);
            var second = await DetectChunkAsync(halves.Second, run, ct, depth + 1);
            return [.. first, .. second];
        }
    }

    // Takes the gate, then runs with its own time budget; the gate is released on every path.
    internal static async Task<T> GatedAsync<T>(
        LlmGate gate, LlmPriority priority, TimeSpan timeout, TimeProvider clock, Func<CancellationToken, Task<T>> run, CancellationToken ct)
    {
        await gate.WaitAsync(priority, ct);
        try
        {
            using var budget = new CancellationTokenSource(timeout, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            try
            {
                return await run(linked.Token);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException("A detector run exceeded its time budget.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    // Halves at the sentence end (else the space) nearest the middle; null when the chunk has no space left to cut at.
    internal static (string First, string Second)? SplitInHalf(string chunk)
    {
        var middle = chunk.Length / 2;
        int Nearest(Func<int, bool> isCut)
        {
            for (var d = 0; d <= middle; d++)
            {
                if (middle - d > 0 && isCut(middle - d))
                {
                    return middle - d;
                }
                if (middle + d < chunk.Length && isCut(middle + d))
                {
                    return middle + d;
                }
            }
            return -1;
        }
        var cut = Nearest(i => char.IsWhiteSpace(chunk[i]) && i > 0 && ".!?…\n".Contains(chunk[i - 1]));
        if (cut < 0)
        {
            cut = Nearest(i => char.IsWhiteSpace(chunk[i]));
        }
        if (cut <= 0)
        {
            return null;
        }
        var first = chunk[..cut].Trim();
        var second = chunk[cut..].Trim();
        return first.Length == 0 || second.Length == 0 ? null : (first, second);
    }

    private async Task<(string Output, bool Truncated)> RunAsync(LLamaWeights weights, ModelParams parameters, string prompt, int promptTokens, CancellationToken ct)
    {
        var needed = (long)promptTokens + OutputTokens + Margin;
        var size = (uint)Math.Clamp((needed + ContextStep - 1) / ContextStep * ContextStep, ContextStep, options.ContextSize);
        var threads = options.ResolvedThreads;
        var executor = new StatelessExecutor(weights, new ModelParams(parameters.ModelPath)
        {
            ContextSize = size,
            Threads = threads,
            BatchThreads = threads,
        });
        using var sampling = new DefaultSamplingPipeline
        {
            Temperature = options.Temperature,
            Grammar = new Grammar(Gbnf, "root"),
        };
        var inference = new InferenceParams
        {
            MaxTokens = OutputTokens,
            AntiPrompts = ["<|im_end|>"],
            SamplingPipeline = sampling,
        };
        var output = new StringBuilder();
        var generated = 0;
        // One piece per sampled token (as in LlamaNoteStructurer), so the count says whether the budget cut the output.
        await foreach (var piece in executor.InferAsync(prompt, inference, ct))
        {
            generated++;
            output.Append(piece);
        }
        return (output.ToString().Replace("<|im_end|>", "").Trim(), generated >= OutputTokens);
    }

    // ChatML prompt for Qwen2.5 Instruct. The text and the names are stripped of control strings: the prompt is tokenised
    // with special tokens enabled.
    internal static string Prompt(string text, IReadOnlyCollection<string> exclusions)
    {
        var excluded = exclusions
            .Select(e => NotePrompt.StripControl(e).Replace('"', ' ').Trim())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var system = new StringBuilder()
            .AppendLine("Você encontra, no texto de um atendimento de psicologia, as menções a terceiros que identificam alguém: pessoas, organizações, escolas e endereços.")
            .AppendLine("Regras:")
            .AppendLine("1. Copie cada menção exatamente como aparece no texto: mesmas letras, maiúsculas, acentos e espaços. Não corrija nem complete.")
            .AppendLine("2. Tipos (\"kind\"):")
            .AppendLine("   - \"person\": nome ou apelido de uma pessoa (ex.: Joana, seu Antônio, Dr. Paulo Mendes).")
            .AppendLine("   - \"organization\": empresa, loja, órgão público, hospital, clínica, igreja, clube ou outra organização com nome próprio.")
            .AppendLine("   - \"school\": escola, colégio, creche, faculdade ou universidade com nome próprio.")
            .AppendLine("   - \"address\": endereço ou localização precisa (rua, avenida, número, condomínio, bairro com referência).")
            .AppendLine("3. Não liste palavras de parentesco ou pronomes sem nome próprio (mãe, pai, irmã, marido, chefe, ele, ela, a gente).")
            .AppendLine("4. Não liste cidades, estados, países, profissões, medicamentos, diagnósticos, datas nem números soltos.")
            .AppendLine(excluded.Count > 0
                ? $"5. Nunca liste estes nomes (o próprio paciente e o profissional): {string.Join(", ", excluded.Select(e => $"\"{e}\""))}."
                : "5. Nunca liste o nome do próprio paciente nem o do profissional.")
            .AppendLine("6. Liste cada menção uma vez. Se não houver nenhuma, responda {\"entities\":[]}.")
            .Append("7. Responda somente com o objeto JSON.");
        return $"<|im_start|>system\n{system.ToString().ReplaceLineEndings("\n")}<|im_end|>\n<|im_start|>user\nTexto:\n{NotePrompt.StripControl(text)}<|im_end|>\n<|im_start|>assistant\n";
    }

    // The JSON object the prompt asks for, with the four kinds only; strings as in NoteGrammar.
    internal static readonly string Gbnf = string.Join('\n',
        "root ::= \"{\" ws \"\\\"entities\\\"\" ws \":\" ws \"[\" ws items? ws \"]\" ws \"}\"",
        "items ::= item (ws \",\" ws item)*",
        "item ::= \"{\" ws \"\\\"text\\\"\" ws \":\" ws string ws \",\" ws \"\\\"kind\\\"\" ws \":\" ws kind ws \"}\"",
        "kind ::= \"\\\"person\\\"\" | \"\\\"organization\\\"\" | \"\\\"school\\\"\" | \"\\\"address\\\"\"",
        "string ::= \"\\\"\" char char* \"\\\"\"",
        "char ::= [^\"\\\\\\x00-\\x1f] | \"\\\\\" ([\"\\\\/bfnrt] | \"u\" hex hex hex hex)",
        "hex ::= [0-9a-fA-F]",
        "ws ::= [ \\t\\n]?");

    // Model output to (text, kind) pairs. Anything but the expected object is a FormatException without the output.
    internal static IReadOnlyList<(string Text, string Kind)> Parse(string json)
    {
        Output? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Output>(json);
        }
        catch (JsonException)
        {
            throw new FormatException("Detector output is not the expected JSON object.");
        }
        if (parsed?.Entities is null || parsed.Entities.Any(e => e is null || e.Text is null || !EntityKinds.IsKnown(e.Kind)))
        {
            throw new FormatException("Detector output does not have the expected entities.");
        }
        return [.. parsed.Entities.Select(e => (e!.Text!, e.Kind!))];
    }

    // Every occurrence of each returned string, on word boundaries, except excluded names.
    internal static IReadOnlyList<DetectedSpan> Locate(string text, IReadOnlyList<(string Text, string Kind)> found, IReadOnlyCollection<string> exclusions)
    {
        var excluded = exclusions.Select(PseudonymTokens.Normalize).ToHashSet(StringComparer.Ordinal);
        var spans = new List<DetectedSpan>();
        foreach (var (surface, kind) in found.Select(f => (f.Text.Trim(), f.Kind)).Distinct())
        {
            if (surface.Length == 0 || excluded.Contains(PseudonymTokens.Normalize(surface)))
            {
                continue;
            }
            for (var at = text.IndexOf(surface, StringComparison.Ordinal); at >= 0; at = text.IndexOf(surface, at + 1, StringComparison.Ordinal))
            {
                if (IsBoundary(text, at - 1) && IsBoundary(text, at + surface.Length))
                {
                    spans.Add(new DetectedSpan(at, surface.Length, kind));
                }
            }
        }
        return spans;
    }

    // The text in runs that each fit `budget` tokens, cut at sentence ends (a longer sentence is cut at words).
    internal static IReadOnlyList<string> Chunks(string text, int budget, Func<string, int> countTokens)
    {
        var pieces = new List<(string Text, int Tokens)>();
        foreach (var sentence in SentenceEnd().Split(text).Where(s => s.Length > 0))
        {
            var tokens = countTokens(sentence);
            if (tokens <= budget)
            {
                pieces.Add((sentence, tokens));
                continue;
            }
            var part = new StringBuilder();
            var used = 0;
            foreach (var word in sentence.Split(' '))
            {
                var wordTokens = countTokens(word) + 1;
                if (part.Length > 0 && used + wordTokens > budget)
                {
                    pieces.Add((part.ToString(), used));
                    part.Clear();
                    used = 0;
                }
                part.Append(part.Length > 0 ? " " : "").Append(word);
                used += wordTokens;
            }
            if (part.Length > 0)
            {
                pieces.Add((part.ToString(), used));
            }
        }

        var chunks = new List<string>();
        var current = new StringBuilder();
        var total = 0;
        foreach (var (piece, tokens) in pieces)
        {
            if (current.Length > 0 && total + tokens + 1 > budget)
            {
                chunks.Add(current.ToString());
                current.Clear();
                total = 0;
            }
            current.Append(current.Length > 0 ? " " : "").Append(piece);
            total += tokens + (current.Length > piece.Length ? 1 : 0);
        }
        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }
        return chunks;
    }

    private static bool IsBoundary(string text, int index) =>
        index < 0 || index >= text.Length || !char.IsLetterOrDigit(text[index]) && char.GetUnicodeCategory(text[index]) != System.Globalization.UnicodeCategory.NonSpacingMark;

    [GeneratedRegex(@"(?<=[.!?…\n])\s+")]
    private static partial Regex SentenceEnd();

    private sealed record Output([property: JsonPropertyName("entities")] List<Item?>? Entities);

    private sealed record Item([property: JsonPropertyName("text")] string? Text, [property: JsonPropertyName("kind")] string? Kind);
}
