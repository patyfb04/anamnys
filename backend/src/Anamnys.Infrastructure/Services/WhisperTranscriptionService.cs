using Anamnys.Application.Interfaces;
using FFMpegCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.Ggml;

namespace Anamnys.Infrastructure.Services;

public class WhisperOptions
{
    /// <summary>
    /// Path to the GGML model file. Must be a multilingual model (no ".en" suffix, e.g.
    /// ggml-medium.bin) — English-only variants (ggml-medium.en.bin) cannot transcribe
    /// other languages at all, regardless of the language setting used at inference time.
    /// </summary>
    public string ModelPath { get; set; } = "models/ggml-medium.bin";
    public GgmlType ModelType { get; set; } = GgmlType.Medium;
}

/// <summary>
/// Batch transcription using Whisper.net (MIT) — fully self-hosted, no API cost.
/// Loads the model lazily and reuses it across requests (thread-safe).
/// </summary>
public class WhisperTranscriptionService : ITranscriptionService, IAsyncDisposable
{
    private readonly WhisperOptions _opts;
    private readonly ILogger<WhisperTranscriptionService> _logger;
    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    public WhisperTranscriptionService(
        IOptions<WhisperOptions> opts,
        ILogger<WhisperTranscriptionService> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs Whisper inference on a local audio file and returns the full transcript as a string.
    /// Called by PipelineJob (batch mode) and TranscriptionHub.FinalizeStream (live mode).
    /// Audio never leaves the server — HIPAA-compliant by design (no third-party STT API).
    /// </summary>
    public async Task<string> TranscribeAsync(string audioFilePath, string? language = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // Whisper.net only reads raw 16-bit PCM WAV; callers hand us whatever the source
        // recorded (m4a, webm, already-wav, etc.), so always transcode first.
        var wavPath = await TranscodeToWavAsync(audioFilePath, ct);
        try
        {
            // Create a fresh processor per request (not thread-safe to share one).
            // "auto" lets Whisper detect the spoken language per-clip; a caller-supplied
            // language (e.g. a patient's preferred language) forces that language instead,
            // which is both faster and more accurate than auto-detection when known upfront.
            using var processor = _factory!
                .CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language)
                .Build();

            using var audioStream = File.OpenRead(wavPath);

            // ProcessAsync yields segments as Whisper decodes them
            var segments = new List<string>();
            await foreach (var segment in processor.ProcessAsync(audioStream, ct))
            {
                segments.Add(segment.Text.Trim());
            }

            var transcript = string.Join(" ", segments.Where(s => !string.IsNullOrWhiteSpace(s)));
            _logger.LogInformation("Transcribed {Chars} chars from {File}", transcript.Length, Path.GetFileName(audioFilePath));
            return transcript;
        }
        finally
        {
            if (File.Exists(wavPath)) File.Delete(wavPath);
        }
    }

    /// <summary>
    /// Re-encodes any source audio (m4a, webm, already-wav, etc.) into a fresh 16kHz mono
    /// PCM WAV file via ffmpeg — the only format Whisper.net can actually read.
    /// </summary>
    private static async Task<string> TranscodeToWavAsync(string sourcePath, CancellationToken ct)
    {
        var wavPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        try
        {
            await FFMpegArguments
                .FromFileInput(sourcePath)
                .OutputToFile(wavPath, overwrite: true, options => options
                    .WithCustomArgument("-ar 16000 -ac 1 -c:a pcm_s16le")
                    .ForceFormat("wav"))
                .CancellableThrough(ct)
                .ProcessAsynchronously();
            return wavPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (File.Exists(wavPath)) File.Delete(wavPath);

            // FFMpegCore wraps a missing binary in its own internal exception types (which have
            // varied across versions) with the real System.ComponentModel.Win32Exception (error
            // code 2 = file not found) nested a couple of levels down — walk the chain instead of
            // pattern-matching a specific wrapper type.
            if (FindInnerException<System.ComponentModel.Win32Exception>(ex) is { NativeErrorCode: 2 })
            {
                throw new InvalidOperationException(
                    "ffmpeg was not found. Install it and ensure 'ffmpeg' is on PATH " +
                    "(the Docker image installs it via apt-get; on Windows, add ffmpeg's /bin folder " +
                    "to PATH and restart Visual Studio).", ex);
            }

            throw new InvalidOperationException(
                $"Audio conversion failed for '{Path.GetFileName(sourcePath)}': {ex.Message}", ex);
        }
    }

    private static TException? FindInnerException<TException>(Exception ex) where TException : Exception
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is TException match) return match;
        }
        return null;
    }

    /// <summary>
    /// Lazy-initializes the WhisperFactory (loads model weights into memory).
    /// Uses a semaphore so concurrent requests don't race to download or load the model.
    /// On first call: auto-downloads the GGML model file from Hugging Face if not present.
    /// Subsequent calls return immediately — model stays loaded for the process lifetime.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_initialized) return; // double-check after acquiring lock

            // Auto-download model if not present (development convenience)
            // GetGgmlModelAsync returns a Stream — we write it to disk ourselves
            if (!File.Exists(_opts.ModelPath))
            {
                _logger.LogInformation("Downloading Whisper model {Type}…", _opts.ModelType);
                Directory.CreateDirectory(Path.GetDirectoryName(_opts.ModelPath)!);
                using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(
                    _opts.ModelType, cancellationToken: ct);
                using var fileStream = File.OpenWrite(_opts.ModelPath);
                await modelStream.CopyToAsync(fileStream, ct);
            }

            _factory = WhisperFactory.FromPath(_opts.ModelPath);
            _initialized = true;
            _logger.LogInformation("Whisper model loaded from {Path}", _opts.ModelPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Releases the WhisperFactory (unloads model weights) on app shutdown.</summary>
    public async ValueTask DisposeAsync()
    {
        _factory?.Dispose();
        await ValueTask.CompletedTask;
    }
}
