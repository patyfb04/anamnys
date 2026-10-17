using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.NoteStructuring;

// Structures a transcript with the in-process LLM. The GBNF grammar only admits a JSON object
// with the format's keys, in order, so the output is parsed rather than trusted.
public sealed class LlamaNoteStructurer(Func<CancellationToken, Task<(LLamaWeights Weights, ModelParams Params)>> load, NoteStructuringOptions options) : INoteStructurer
{
    // The output is a cleaned redistribution of the dictation, so it is about as long as the
    // transcript (estimated at 1.2×); each section also costs its JSON key and quoting.
    private const int OutputPerSection = 64;

    public LlamaNoteStructurer(LlmModelProvider models, IOptions<NoteStructuringOptions> options)
        : this(models.GetAsync, options.Value) { }

    // Test-facing: loads weights from a local path without downloading or verifying. The caller
    // owns the returned weights.
    internal static (LlamaNoteStructurer Structurer, LLamaWeights Weights) ForModelFile(string path, NoteStructuringOptions options)
    {
        var parameters = new ModelParams(path) { ContextSize = options.ContextSize, Threads = options.ResolvedThreads };
        var weights = LLamaWeights.LoadFromFile(parameters);
        return (new LlamaNoteStructurer(_ => Task.FromResult((weights, parameters)), options), weights);
    }

    public async Task<IReadOnlyList<StructuredSection>> StructureAsync(string transcript, string format, NoteSource source, CancellationToken ct)
    {
        var keys = NoteFormats.KeysFor(format);
        var (weights, parameters) = await load(ct);
        var prompt = NotePrompt.Build(transcript, format, source);
        var promptTokens = weights.Tokenize(prompt, add_bos: true, special: true, Encoding.UTF8).Length;
        var transcriptTokens = weights.Tokenize(transcript, add_bos: false, special: false, Encoding.UTF8).Length;
        if (!Fits(promptTokens, transcriptTokens, keys.Count, options.ContextSize))
        {
            throw new TranscriptTooLongException();
        }

        var maxTokens = OutputBudget(promptTokens, options.ContextSize, options.MaxOutputTokens);
        var executor = new StatelessExecutor(weights, parameters);
        using var sampling = new DefaultSamplingPipeline
        {
            Temperature = options.Temperature,
            Grammar = new Grammar(NoteGrammar.For(keys), "root"),
        };
        var inference = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = ["<|im_end|>"],
            SamplingPipeline = sampling,
        };
        var output = new StringBuilder();
        var generated = 0;
        // StatelessExecutor yields one piece per sampled token (possibly empty while a multi-byte
        // character is incomplete), so the piece count is the generated token count.
        await foreach (var piece in executor.InferAsync(prompt, inference, ct))
        {
            generated++;
            output.Append(piece);
        }
        try
        {
            return Parse(output.ToString().Replace("<|im_end|>", "").Trim(), keys);
        }
        catch (FormatException) when (generated >= maxTokens)
        {
            // Generation hit the token budget mid-JSON: a retry would be cut the same way.
            throw new TranscriptTooLongException();
        }
    }

    // Whether the prompt plus the expected output (about the transcript's length) fits the context.
    internal static bool Fits(int promptTokens, int transcriptTokens, int sectionCount, uint contextSize)
    {
        // ceil(transcriptTokens × 6/5) in integers, so the boundary does not depend on rounding.
        var expectedOutput = (transcriptTokens * 6L + 4) / 5 + (long)OutputPerSection * sectionCount;
        return promptTokens + expectedOutput <= contextSize;
    }

    // Tokens generation may use: whatever the context leaves after the prompt, capped by
    // MaxOutputTokens only when that is configured (> 0).
    internal static int OutputBudget(int promptTokens, uint contextSize, int maxOutputTokens)
    {
        var budget = (int)Math.Max(0, contextSize - (long)promptTokens);
        return maxOutputTokens > 0 ? Math.Min(budget, maxOutputTokens) : budget;
    }

    internal static IReadOnlyList<StructuredSection> Parse(string json, IReadOnlyList<string> keys)
    {
        Dictionary<string, string>? values;
        try
        {
            values = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException e)
        {
            throw new FormatException("Model output is not the expected JSON object.", e);
        }
        if (values is null || values.Count != keys.Count || keys.Any(k => !values.ContainsKey(k)))
        {
            throw new FormatException("Model output does not have the format's sections.");
        }
        return keys.Select(k => new StructuredSection(k, values[k])).ToList();
    }
}
