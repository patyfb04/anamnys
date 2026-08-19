using Anamnys.Application.Interfaces;
using Anamnys.Domain.Entities;
using Anamnys.Domain.Enums;
using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anamnys.Infrastructure.Services;

public class LlamaOptions
{
    /// <summary>Path to the GGUF model file (Phi-4 14B Q4_K_M or Mistral 7B Q4_K_M).</summary>
    public string ModelPath { get; set; } = "models/phi-4-14b-q4_k_m.gguf";
    public uint ContextSize { get; set; } = 4096;
    public int GpuLayerCount { get; set; } = 0; // Set > 0 if GPU available
    public float Temperature { get; set; } = 0.1f;
    public int MaxTokens { get; set; } = 2048;
}

/// <summary>
/// Note structuring using LLamaSharp (MIT) with Phi-4 14B.
/// Converts raw transcript → structured clinical note in the requested format.
/// </summary>
public class LlamaStructuringService : INoteStructuringService, IDisposable
{
    private readonly LlamaOptions _opts;
    private readonly ILogger<LlamaStructuringService> _logger;
    private LLamaWeights? _weights;
    private readonly SemaphoreSlim _lock = new(1, 1); // serialize inference
    private bool _initialized;

    public LlamaStructuringService(
        IOptions<LlamaOptions> opts,
        ILogger<LlamaStructuringService> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs LLM inference to convert raw session transcript into a structured clinical note.
    /// Called by PipelineJob as Step 2 of the background pipeline.
    /// PHI stays on-premises — no cloud LLM API is used (HIPAA-compliant by design).
    /// Serializes inference via a semaphore because LLaMA is not thread-safe.
    /// </summary>
    public async Task<StructuredNote> StructureAsync(
        string rawTranscript,
        NoteFormat format,
        Specialty specialty,
        CancellationToken ct = default)
    {
        EnsureInitialized();

        var systemPrompt = BuildSystemPrompt(format, specialty);
        var userPrompt = $"Raw session notes:\n\n{rawTranscript}\n\nReturn ONLY valid JSON, no markdown fences.";

        string json;
        await _lock.WaitAsync(ct);
        try
        {
            var modelParams = new ModelParams(_opts.ModelPath)
            {
                ContextSize = _opts.ContextSize,
                GpuLayerCount = _opts.GpuLayerCount,
            };

            using var context = _weights!.CreateContext(modelParams);
            var executor = new StatelessExecutor(_weights, modelParams);

            var inferParams = new InferenceParams
            {
                MaxTokens = _opts.MaxTokens,
                Temperature = _opts.Temperature,
            };

            var fullPrompt = $"<|system|>{systemPrompt}<|end|>\n<|user|>{userPrompt}<|end|>\n<|assistant|>";
            var sb = new System.Text.StringBuilder();
            await foreach (var token in executor.InferAsync(fullPrompt, inferParams, ct))
            {
                sb.Append(token);
            }
            json = sb.ToString().Trim();
        }
        finally
        {
            _lock.Release();
        }

        return ParseStructuredNote(json, format);
    }

    /// <summary>
    /// Builds a specialty- and format-specific system prompt for the LLM.
    /// The prompt defines which section headings to produce (e.g. Data/Assessment/Plan for DAP)
    /// and instructs the model to return only valid JSON — no hallucinated facts or patient names.
    /// </summary>
    private static string BuildSystemPrompt(NoteFormat format, Specialty specialty)
    {
        var sections = format switch
        {
            NoteFormat.DAP => new[] { "Data", "Assessment", "Plan" },
            NoteFormat.SOAP => new[] { "Subjective", "Objective", "Assessment", "Plan" },
            NoteFormat.PtFunctional => new[] { "SubjectiveComplaints", "ObjectiveMeasures", "FunctionalLimitations", "Assessment", "Goals", "Plan" },
            NoteFormat.Biopsychosocial => new[] { "Biological", "Psychological", "Social", "Assessment", "Plan" },
            _ => new[] { "Subjective", "Objective", "Assessment", "Plan" }
        };

        var sectionsList = string.Join(", ", sections.Select(s => $"\"{s}\""));
        var specialtyStr = specialty == Specialty.MentalHealth ? "mental health (psychotherapy)" : "physical therapy";

        return $"""
            You are a {specialtyStr} clinical documentation assistant.
            Given raw session notes, produce a structured {format} clinical note.
            Return ONLY a JSON object with a "sections" key containing these fields: {sectionsList}.
            Each field value must be a clear, professional clinical narrative.
            Do not invent facts not present in the input. Do not add patient names or dates.
            """;
    }

    /// <summary>
    /// Parses the LLM's JSON output into a StructuredNote.
    /// Uses a regex to extract the JSON object in case the model adds preamble text.
    /// Falls back gracefully if JSON is malformed — avoids losing the note entirely.
    /// </summary>
    private static StructuredNote ParseStructuredNote(string json, NoteFormat format)
    {
        // Extract JSON object from response (model may add extra text)
        var match = Regex.Match(json, @"\{[\s\S]*\}");
        if (match.Success) json = match.Value;

        Dictionary<string, string> sections;
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sections", out var sectionsEl))
            {
                sections = sectionsEl.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
            }
            else
            {
                // Flat object fallback
                sections = doc.RootElement.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
            }
        }
        catch
        {
            // Fallback: put everything in a single section
            sections = new Dictionary<string, string> { ["Note"] = json };
        }

        return new StructuredNote
        {
            Format = format,
            Sections = sections,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Loads LLaMA model weights into memory on first use (can take 10–30s for large models).
    /// Unlike Whisper, LLaMA weights cannot be auto-downloaded — the model file must exist
    /// at the configured path. Throws FileNotFoundException with actionable guidance if missing.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_initialized) return;

        if (!File.Exists(_opts.ModelPath))
            throw new FileNotFoundException(
                $"LLaMA model not found at '{_opts.ModelPath}'. " +
                "Download Phi-4-14b-q4_k_m.gguf and place it at the configured path.");

        var modelParams = new ModelParams(_opts.ModelPath)
        {
            ContextSize = _opts.ContextSize,
            GpuLayerCount = _opts.GpuLayerCount,
        };
        _weights = LLamaWeights.LoadFromFile(modelParams);
        _initialized = true;
    }

    public void Dispose()
    {
        _weights?.Dispose();
    }
}
