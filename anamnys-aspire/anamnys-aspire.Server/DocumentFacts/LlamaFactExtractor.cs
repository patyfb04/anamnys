using System.Text;
using Anamnys.Server.NoteStructuring;
using Anamnys.Server.Themes;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.DocumentFacts;

// Extracts declared facts with the process's Qwen weights (LlmModelProvider) under the gate shared with
// theme suggestion (LlmGate): one model run at a time. Each run gets a context of its own, sized to its
// prompt plus the output, and disposed after it (the suggester's cached context is not touched).
//
// Pages are packed in order into runs that fit NoteStructuring:ContextSize; a page that alone does not
// fit is cut to a prefix. The gate is taken per run at background priority, and inside a run the
// extractor yields it (LlmGate.YieldAsync) between decode steps, so a suggestion the provider is waiting
// for runs after at most one step instead of after the whole extraction.
// The grammar (FactGrammar.For) admits only the given page numbers; the fixed JSON around the sampled
// parts is fed without sampling (FactGrammar.Forced), as in LlamaThemeSuggester.
public sealed class LlamaFactExtractor(
    Func<CancellationToken, Task<(LLamaWeights Weights, ModelParams Params)>> load, NoteStructuringOptions options, LlmGate gate)
    : IFactExtractor
{
    // Sampled tokens per run (twelve facts with a short quote and value each, plus headroom).
    internal const int OutputTokens = 1200;
    // Fixed JSON per item fed without sampling.
    internal const int ForcedTokensPerItem = 24;
    internal const int Margin = 32;
    // Prompt tokens decoded per step, so one step (the longest an interactive request waits) stays short.
    internal const int StepTokens = 128;
    private const int ContextStep = 256;
    private const int MinContext = 1024;

    public LlamaFactExtractor(LlmModelProvider models, IOptions<NoteStructuringOptions> options, LlmGate gate)
        : this(models.GetAsync, options.Value, gate) { }

    public async Task<IReadOnlyList<FactCandidate>> ExtractAsync(IReadOnlyList<FactPage> pages, CancellationToken ct)
    {
        var useful = pages.Where(p => p.PageNumber >= 1 && !string.IsNullOrWhiteSpace(p.Text)).OrderBy(p => p.PageNumber).ToList();
        if (useful.Count == 0)
        {
            return [];
        }
        var (weights, parameters) = await load(ct);
        int Count(string text, bool special) => weights.Tokenize(text, false, special, Encoding.UTF8).Length;

        var fixedTokens = Count(FactPrompt.Prefix(), true) + 1 + Count(FactPrompt.Instruction + FactPrompt.Suffix, true);
        var budget = TextBudget(options.ContextSize, fixedTokens);
        if (budget <= 0)
        {
            throw new InvalidOperationException("The fact prompt does not fit the model context.");
        }

        var result = new List<FactCandidate>();
        foreach (var run in Pack(useful, budget, (n, t) => Count(FactPrompt.Page(n, t), false)))
        {
            var fitted = run.Select(p => Count(FactPrompt.Page(p.PageNumber, p.Text), false) > budget
                ? p with { Text = ThemePrompt.Fit(p.Text, budget - 16, s => Count(FactPrompt.Page(p.PageNumber, s), false)) }
                : p).ToList();
            await gate.WaitAsync(LlmPriority.Background, ct);
            try
            {
                var output = await RunAsync(weights, parameters, fitted, ct);
                result.AddRange(FactGrammar.Parse(output));
            }
            finally
            {
                gate.Release();
            }
        }
        return result;
    }

    // Tokens left for page text in one run.
    internal static int TextBudget(uint contextSize, int fixedTokens) =>
        (int)Math.Min(int.MaxValue, (long)contextSize - fixedTokens - OutputTokens - (long)FactGrammar.MaxItems * ForcedTokensPerItem - Margin);

    // Context for a run: the prompt, the output and the forced JSON, rounded up, within the configured size.
    internal static uint ContextFor(int promptTokens, uint maxContext)
    {
        var needed = (long)promptTokens + OutputTokens + (long)FactGrammar.MaxItems * ForcedTokensPerItem + Margin;
        var rounded = (needed + ContextStep - 1) / ContextStep * ContextStep;
        return (uint)Math.Clamp(rounded, MinContext, maxContext);
    }

    // Pages in order, grouped so each group's text fits budget (a page larger than budget is a group alone).
    internal static IReadOnlyList<IReadOnlyList<FactPage>> Pack(IReadOnlyList<FactPage> pages, int budget, Func<int, string, int> countTokens)
    {
        var runs = new List<IReadOnlyList<FactPage>>();
        var current = new List<FactPage>();
        var used = 0;
        foreach (var page in pages)
        {
            var tokens = countTokens(page.PageNumber, page.Text);
            if (current.Count > 0 && used + tokens > budget)
            {
                runs.Add(current);
                current = [];
                used = 0;
            }
            current.Add(page);
            used += tokens;
        }
        if (current.Count > 0)
        {
            runs.Add(current);
        }
        return runs;
    }

    // One model run in a fresh context; returns the raw output. The caller holds the gate.
    private async Task<string> RunAsync(LLamaWeights weights, ModelParams parameters, IReadOnlyList<FactPage> pages, CancellationToken ct)
    {
        var prompt = weights.Tokenize(FactPrompt.Prefix(), true, true, Encoding.UTF8).ToList();
        foreach (var page in pages)
        {
            // Document text is never parsed for special tokens (a "<|im_end|>" in it stays plain text).
            prompt.AddRange(weights.Tokenize(FactPrompt.Page(page.PageNumber, page.Text), false, false, Encoding.UTF8));
        }
        prompt.AddRange(weights.Tokenize(FactPrompt.Instruction + FactPrompt.Suffix, false, true, Encoding.UTF8));

        var size = ContextFor(prompt.Count, options.ContextSize);
        var threads = options.ResolvedThreads;
        using var ctx = weights.CreateContext(new ModelParams(parameters.ModelPath)
        {
            ContextSize = size,
            Threads = threads,
            BatchThreads = threads,
        });
        using var sampling = new DefaultSamplingPipeline
        {
            Temperature = options.Temperature,
            Grammar = new Grammar(FactGrammar.For(pages.Select(p => p.PageNumber).ToList()), "root"),
        };
        var batch = new LLamaBatch();
        var decoder = new StreamingTokenDecoder(ctx);
        var output = new StringBuilder();
        var pending = new List<LLamaToken>(prompt);
        var position = 0;
        for (var sampled = 0; sampled < OutputTokens;)
        {
            ct.ThrowIfCancellationRequested();
            var current = output.ToString();
            if (FactGrammar.IsComplete(current))
            {
                break;
            }
            // Fixed JSON the grammar admits with no choice, accepted without sampling. Not before the first
            // Sample: the pipeline creates its sampler chains there, and an earlier Accept is lost.
            var forced = sampled == 0 ? "" : FactGrammar.Forced(current);
            if (forced.Length > 0)
            {
                foreach (var token in ctx.Tokenize(forced, addBos: false, special: false))
                {
                    sampling.Accept(token);
                    pending.Add(token);
                }
                output.Append(forced);
                continue;
            }
            if (position + pending.Count >= ctx.ContextSize)
            {
                break;
            }
            // One decode (at most one batch of the prompt, or the next sampled token with its forced JSON) per
            // step; between steps a waiting interactive request (the suggest dialog) takes the gate and this
            // run resumes after it. The context is this run's own, so nothing is lost meanwhile.
            while (pending.Count > 0)
            {
                await gate.YieldAsync();
                ct.ThrowIfCancellationRequested();
                var step = Math.Min(pending.Count, Math.Min(StepTokens, (int)ctx.BatchSize));
                position = await LlamaThemeSuggester.DecodeAsync(ctx, batch, [.. pending.GetRange(0, step)], position, ct);
                pending.RemoveRange(0, step);
            }
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
}
