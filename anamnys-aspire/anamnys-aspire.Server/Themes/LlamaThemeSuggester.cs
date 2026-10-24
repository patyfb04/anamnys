using System.Text;
using Anamnys.Server.NoteStructuring;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.Themes;

// Suggests themes with the same in-process model as structuring (one set of weights, shared through
// LlmModelProvider). The GBNF grammar only admits an array of the vocabulary's codes, and the output
// is still parsed rather than trusted.
//
// Exclusions (terms already tagged, terms the provider removed) are applied to the parsed output,
// not in the prompt nor by narrowing the grammar's code list. Narrowing was measured to make things
// worse: the model commits to an opening quote before it can know a code is forbidden, and is then
// pushed onto loosely related terms (a note about sleep, work and family, with those three excluded,
// came back with four unrelated themes instead of []). The grammar admits up to MaxItems extra items
// so excluded codes the model names do not crowd out the others.
//
// Prefix cache: the prompt starts with a part that depends only on the vocabulary
// (ThemePrompt.Prefix). It is evaluated once into a context of its own (SuggestionContextSize,
// much smaller than the structuring context) and that context is kept for the process. Each call
// drops the KV entries after the prefix, evaluates only the note text and the assistant turn, and
// samples. A change in any code, label or parent changes the prefix key and rebuilds the context
// on the next call; WarmAsync builds it ahead of the first call (the hosted ThemeWarmupService does, at start and
// after vocabulary changes); without it the first call builds it lazily.
public sealed class LlamaThemeSuggester(Func<CancellationToken, Task<(LLamaWeights Weights, ModelParams Params)>> load, NoteStructuringOptions options)
    : IThemeSuggester, IDisposable
{
    // Five quoted codes with separators, plus headroom for tokenisation differences.
    internal const int OutputTokens = 160;
    internal const int Margin = 16;

    // One model run at a time: concurrent suggest requests queue instead of piling up inference,
    // and the cached context is only ever touched under this gate.
    private readonly SemaphoreSlim gate = new(1, 1);
    private LLamaContext? context;
    private string? prefixKey;
    private int prefixTokens;

    public LlamaThemeSuggester(LlmModelProvider models, IOptions<NoteStructuringOptions> options)
        : this(models.GetAsync, options.Value) { }

    public async Task<IReadOnlyList<string>> SuggestAsync(
        string text, IReadOnlyList<ThemeTermOption> terms, IReadOnlyCollection<string> excludedCodes, CancellationToken ct)
    {
        var allowed = AllowedCodes(terms, excludedCodes);
        if (allowed.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        await gate.WaitAsync(ct);
        try
        {
            return await RunAsync(text, terms, excludedCodes, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WarmAsync(IReadOnlyList<ThemeTermOption> terms, CancellationToken ct)
    {
        if (terms.Count == 0)
        {
            return;
        }
        await gate.WaitAsync(ct);
        try
        {
            var (weights, parameters) = await load(ct);
            await PrefixContextAsync(weights, parameters, ThemePrompt.Prefix(terms), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    // The codes the grammar admits: every given term except the excluded ones.
    internal static IReadOnlySet<string> AllowedCodes(IReadOnlyList<ThemeTermOption> terms, IReadOnlyCollection<string> excludedCodes) =>
        terms.Select(t => t.Code).Where(c => !excludedCodes.Contains(c)).ToHashSet(StringComparer.Ordinal);

    // Tokens left for the note text once the prefix, the suffix, the output and a margin are counted.
    internal static int TextBudget(uint contextSize, int prefixTokens, int suffixTokens) =>
        (int)Math.Min(int.MaxValue, (long)contextSize - prefixTokens - suffixTokens - OutputTokens - Margin);

    private async Task<IReadOnlyList<string>> RunAsync(
        string text, IReadOnlyList<ThemeTermOption> terms, IReadOnlyCollection<string> excludedCodes, CancellationToken ct)
    {
        var codes = terms.Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
        var excluded = excludedCodes.Where(codes.Contains).ToHashSet(StringComparer.Ordinal);
        var grammar = ThemeGrammar.For([.. codes], ThemeGrammar.ItemsWithExcluded(excluded.Count));
        var (weights, parameters) = await load(ct);
        var ctx = await PrefixContextAsync(weights, parameters, ThemePrompt.Prefix(terms), ct);

        var suffix = ctx.Tokenize(ThemePrompt.Suffix, addBos: false, special: true);
        var budget = TextBudget(ctx.ContextSize, prefixTokens, suffix.Length);
        if (budget <= 0)
        {
            throw new InvalidOperationException("The theme vocabulary does not fit the suggestion context.");
        }
        var clean = NotePrompt.StripControl(text);
        var fitted = ThemePrompt.Fit(clean, budget, s => ctx.Tokenize(s, addBos: false, special: false).Length);
        LLamaToken[] body = [.. ctx.Tokenize(fitted, addBos: false, special: false), .. suffix];

        // Keep the prefix, forget whatever the previous call appended after it.
        ctx.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, prefixTokens, -1);
        var batch = new LLamaBatch();
        var position = await DecodeAsync(ctx, batch, body, prefixTokens, ct);

        using var sampling = new DefaultSamplingPipeline
        {
            Temperature = options.Temperature,
            Grammar = new Grammar(grammar, "root"),
        };
        var decoder = new StreamingTokenDecoder(ctx);
        var output = new StringBuilder();
        var logits = batch.TokenCount - 1;
        for (var i = 0; i < OutputTokens; i++)
        {
            ct.ThrowIfCancellationRequested();
            // Sample also advances the grammar (llama_sampler_sample accepts the token).
            var token = sampling.Sample(ctx.NativeHandle, logits);
            if (token.IsEndOfGeneration(ctx.Vocab))
            {
                break;
            }
            decoder.Add(token);
            output.Append(decoder.Read());
            // Codes never contain ']': the first one closes the array and nothing else is admitted.
            if (output.ToString().Contains(']'))
            {
                break;
            }
            batch.Clear();
            batch.Add(token, position++, LLamaSeqId.Zero, true);
            var result = await ctx.DecodeAsync(batch, ct);
            if (result != DecodeResult.Ok)
            {
                throw new LLamaDecodeError(result);
            }
            logits = 0;
        }
        return ThemeGrammar.Pick(output.ToString().Replace("<|im_end|>", "").Trim(), codes, excluded);
    }

    // The context holding the evaluated prefix, (re)built when the prefix changed.
    private async Task<LLamaContext> PrefixContextAsync(LLamaWeights weights, ModelParams parameters, string prefix, CancellationToken ct)
    {
        var key = ThemePrompt.PrefixKey(prefix);
        if (context is { } cached && prefixKey == key)
        {
            return cached;
        }
        context?.Dispose();
        context = null;
        prefixKey = null;

        var threads = options.ResolvedSuggestionThreads;
        var ctx = weights.CreateContext(new ModelParams(parameters.ModelPath)
        {
            ContextSize = options.SuggestionContextSize,
            Threads = threads,
            BatchThreads = threads,
        });
        try
        {
            var tokens = ctx.Tokenize(prefix, addBos: true, special: true);
            await DecodeAsync(ctx, new LLamaBatch(), tokens, 0, ct);
            context = ctx;
            prefixKey = key;
            prefixTokens = tokens.Length;
            return ctx;
        }
        catch
        {
            ctx.Dispose();
            throw;
        }
    }

    // Evaluates tokens from position start in BatchSize chunks; only the last token gets logits, and
    // it is the last entry of batch on return. Returns the next free position.
    private static async Task<int> DecodeAsync(LLamaContext ctx, LLamaBatch batch, LLamaToken[] tokens, int start, CancellationToken ct)
    {
        var chunk = (int)ctx.BatchSize;
        for (var offset = 0; offset < tokens.Length; offset += chunk)
        {
            batch.Clear();
            var end = Math.Min(tokens.Length, offset + chunk);
            for (var i = offset; i < end; i++)
            {
                batch.Add(tokens[i], start + i, LLamaSeqId.Zero, i == tokens.Length - 1);
            }
            var result = await ctx.DecodeAsync(batch, ct);
            if (result != DecodeResult.Ok)
            {
                throw new LLamaDecodeError(result);
            }
        }
        return start + tokens.Length;
    }

    public void Dispose()
    {
        context?.Dispose();
        context = null;
        gate.Dispose();
    }
}
