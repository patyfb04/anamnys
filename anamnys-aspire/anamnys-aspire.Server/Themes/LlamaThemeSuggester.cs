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
// LlmModelProvider). The GBNF grammar only admits an array of {code, quote} objects with the
// vocabulary's codes, and the output is still parsed rather than trusted (the caller locates the
// quote in the note before using it). The grammar has fixed spacing, so the JSON around the model's
// choices (keys, separators, the fixed codes of a locate run) is fed without sampling
// (ThemeGrammar.Forced), batched with the next decode: only codes, quote text and the item/end
// decisions are sampled.
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
public sealed class LlamaThemeSuggester(
    Func<CancellationToken, Task<(LLamaWeights Weights, ModelParams Params)>> load, NoteStructuringOptions options, LlmGate? sharedGate = null)
    : IThemeSuggester, IDisposable
{
    // Output cap per mode, used for the generation loop and for the note-text budget. Codes only: ten
    // quoted codes with separators plus headroom (as before F-03, so the fast suggest keeps its text
    // budget). With quotes: up to ten {code, quote} items; quotes are asked to be short, and a run that
    // reaches the cap is recovered (ThemeGrammar.ParseOrRecover).
    internal const int CodesOutputTokens = 160;
    internal const int QuotesOutputTokens = 720;
    // Codes per "locate" run, so one run stays within QuotesOutputTokens.
    internal const int QuoteBatch = 10;
    internal const int Margin = 16;
    // Tokens reserved per item for the fixed JSON that is fed without sampling.
    internal const int ForcedTokensPerItem = 16;

    // One model run at a time: concurrent suggest requests queue instead of piling up inference,
    // and the cached context is only ever touched under this gate. The gate is shared with document
    // fact extraction (LlmGate, F-08); built without one (tests), the suggester has its own.
    private readonly bool ownsGate = sharedGate is null;
    private readonly LlmGate gate = sharedGate ?? new LlmGate();
    private LLamaContext? context;
    private string? prefixKey;
    private int prefixTokens;

    public LlamaThemeSuggester(LlmModelProvider models, IOptions<NoteStructuringOptions> options, LlmGate gate)
        : this(models.GetAsync, options.Value, gate) { }

    public async Task<IReadOnlyList<ThemeCodeQuote>> SuggestAsync(
        string text, IReadOnlyList<ThemeTermOption> terms, IReadOnlyCollection<string> excludedCodes, bool withQuotes,
        CancellationToken ct)
    {
        var allowed = AllowedCodes(terms, excludedCodes);
        if (allowed.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var codes = terms.Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
        var excluded = excludedCodes.Where(codes.Contains).ToHashSet(StringComparer.Ordinal);
        var items = ThemeGrammar.ItemsWithExcluded(excluded.Count);
        // Codes only is the suggest dialog, where the provider waits: it goes before background runs
        // (document fact extraction yields to it between steps). With quotes is structuring's proposals.
        await gate.WaitAsync(withQuotes ? LlmPriority.Background : LlmPriority.Interactive, ct);
        try
        {
            if (!withQuotes)
            {
                var plain = await RunAsync(text, terms, false, 0, ThemeGrammar.ForCodes([.. codes], items), _ => "", ct);
                return ThemeGrammar.PickCodes(plain, codes, excluded);
            }
            var output = await RunAsync(text, terms, true, items, ThemeGrammar.For([.. codes], items), o => ThemeGrammar.Forced(o, items), ct);
            return ThemeGrammar.Pick(output, codes, excluded);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<ThemeCodeQuote>> QuoteAsync(
        string text, IReadOnlyList<ThemeTermOption> terms, IReadOnlyList<string> codes, CancellationToken ct)
    {
        var known = terms.Select(t => t.Code).ToHashSet(StringComparer.Ordinal);
        var wanted = codes.Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var result = new List<ThemeCodeQuote>();
        await gate.WaitAsync(LlmPriority.Background, ct);
        try
        {
            foreach (var batch in wanted.Chunk(QuoteBatch))
            {
                var output = await RunAsync(text, terms, true, batch.Length, ThemeGrammar.ForQuotes(batch), o => ThemeGrammar.Forced(o, batch.Length, batch), ct);
                result.AddRange(ThemeGrammar.ParseOrRecover(output, batch, batch.Length));
            }
            return result;
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
        await gate.WaitAsync(LlmPriority.Background, ct);
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
    internal static int TextBudget(uint contextSize, int prefixTokens, int suffixTokens, bool withQuotes, int items = 0) =>
        (int)Math.Min(int.MaxValue, (long)contextSize - prefixTokens - suffixTokens - OutputTokensFor(withQuotes) - (long)items * ForcedTokensPerItem - Margin);

    internal static int OutputTokensFor(bool withQuotes) => withQuotes ? QuotesOutputTokens : CodesOutputTokens;

    // One model run over the cached prefix with the given grammar; returns the raw output. The caller holds the gate.
    private async Task<string> RunAsync(
        string text, IReadOnlyList<ThemeTermOption> terms, bool withQuotes, int items, string grammar, Func<string, string> forcedAfter,
        CancellationToken ct)
    {
        var (weights, parameters) = await load(ct);
        var ctx = await PrefixContextAsync(weights, parameters, ThemePrompt.Prefix(terms), ct);

        // The mode's instruction and the assistant turn, after the note (the prefix is shared by both modes).
        var suffix = ctx.Tokenize(ThemePrompt.Instruction(withQuotes) + ThemePrompt.Suffix, addBos: false, special: true);
        var budget = TextBudget(ctx.ContextSize, prefixTokens, suffix.Length, withQuotes, items);
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
        var position = prefixTokens;

        using var sampling = new DefaultSamplingPipeline
        {
            Temperature = options.Temperature,
            Grammar = new Grammar(grammar, "root"),
        };
        var decoder = new StreamingTokenDecoder(ctx);
        var output = new StringBuilder();
        // Tokens not yet evaluated: the note and the assistant turn first, then each sampled token
        // together with the forced text that follows it, so fixed JSON costs one batched decode
        // instead of one decode per token.
        var pending = new List<LLamaToken>(body);
        var cap = OutputTokensFor(withQuotes);
        var forcedTokens = 0;
        for (var sampled = 0; sampled + forcedTokens < cap;)
        {
            ct.ThrowIfCancellationRequested();
            var current = output.ToString();
            if (ThemeGrammar.IsComplete(current))
            {
                break;
            }
            // Text the grammar admits with no choice ("{\"code\": \"", "\", \"quote\": \"", "}", the
            // fixed codes of a locate run…): accepted into the grammar without sampling. Not before the
            // first Sample: the pipeline creates its sampler chains there, and an earlier Accept is lost
            // (the grammar would then reject what follows). The model writes the opening "[" itself.
            var forced = sampled == 0 ? "" : forcedAfter(current);
            if (forced.Length > 0)
            {
                foreach (var token in ctx.Tokenize(forced, addBos: false, special: false))
                {
                    sampling.Accept(token);
                    pending.Add(token);
                    forcedTokens++;
                }
                output.Append(forced);
                continue;
            }
            position = await DecodeAsync(ctx, batch, [.. pending], position, ct);
            pending.Clear();
            // Sample also advances the grammar (llama_sampler_sample accepts the token).
            var next = sampling.Sample(ctx.NativeHandle, batch.TokenCount - 1);
            sampled++;
            if (next.IsEndOfGeneration(ctx.Vocab))
            {
                break;
            }
            decoder.Add(next);
            output.Append(decoder.Read());
            pending.Add(next);
        }
        return output.ToString().Replace("<|im_end|>", "").Trim();
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
    internal static async Task<int> DecodeAsync(LLamaContext ctx, LLamaBatch batch, LLamaToken[] tokens, int start, CancellationToken ct)
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
        if (ownsGate)
        {
            gate.Dispose();
        }
    }
}
