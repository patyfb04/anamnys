using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Anamnys.Application.Interfaces;
using System.Security.Claims;

namespace Anamnys.Api.Hubs;

/// <summary>
/// Real-time transcription hub for Mode B (live dictation).
/// The mobile app sends raw audio chunks; the server streams them through Whisper
/// and pushes partial/final transcript text back.
/// </summary>
[Authorize]
public class TranscriptionHub : Hub
{
    private readonly ITranscriptionService _transcription;
    private readonly ILogger<TranscriptionHub> _logger;

    // ~25MB — several minutes of compressed audio; guards against unbounded memory growth
    // from a buggy or malicious client that never calls FinalizeStream.
    private const long MaxBufferBytes = 25 * 1024 * 1024;

    // Per-connection audio buffer
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<byte>>
        AudioBuffers = new();

    // Connections that exceeded MaxBufferBytes — further chunks are ignored until they finalize/reconnect.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool>
        AbortedConnections = new();

    public TranscriptionHub(ITranscriptionService transcription, ILogger<TranscriptionHub> logger)
    {
        _transcription = transcription;
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        AudioBuffers[Context.ConnectionId] = new List<byte>();
        AbortedConnections.TryRemove(Context.ConnectionId, out _);
        _logger.LogInformation("TranscriptionHub: {ConnId} connected", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        AudioBuffers.TryRemove(Context.ConnectionId, out _);
        AbortedConnections.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Receives a raw audio chunk from the mobile client and buffers it.
    ///
    /// NOTE: expo-av exposes no incremental PCM access during recording on any platform,
    /// so there is no true streaming/partial transcription — the client sends the whole
    /// recording in chunks after stopping, and FinalizeStream transcribes it in one pass.
    /// </summary>
    public async Task SendAudioChunk(byte[] chunk)
    {
        var connId = Context.ConnectionId;
        if (AbortedConnections.ContainsKey(connId)) return;
        if (!AudioBuffers.TryGetValue(connId, out var buffer)) return;

        if (buffer.Count + chunk.Length > MaxBufferBytes)
        {
            AbortedConnections[connId] = true;
            buffer.Clear();
            _logger.LogWarning("TranscriptionHub: {ConnId} exceeded max buffer size ({MaxMb}MB)",
                connId, MaxBufferBytes / (1024 * 1024));
            await Clients.Caller.SendAsync("TranscriptionError",
                $"Recording exceeded the maximum allowed size ({MaxBufferBytes / (1024 * 1024)}MB). Please record a shorter clip.");
            return;
        }

        buffer.AddRange(chunk);
    }

    /// <summary>
    /// Client signals end of stream. Runs Whisper (via a transcode-to-WAV step) on the
    /// accumulated buffer and returns the full transcript in one shot.
    /// </summary>
    /// <param name="language">
    /// ISO 639-1 code to force a specific language (e.g. the patient's preferred language,
    /// resolved client-side), or null/empty to auto-detect.
    /// </param>
    public async Task FinalizeStream(string patientId, string format, string? language = null)
    {
        var connId = Context.ConnectionId;

        if (AbortedConnections.TryRemove(connId, out _))
        {
            await Clients.Caller.SendAsync("TranscriptionError", "Recording was too large and was discarded. Please try again.");
            return;
        }

        if (!AudioBuffers.TryGetValue(connId, out var buffer) || buffer.Count == 0)
        {
            await Clients.Caller.SendAsync("FinalTranscript", string.Empty);
            return;
        }

        if (!Domain.SupportedLanguages.IsValid(language))
        {
            await Clients.Caller.SendAsync("TranscriptionError", $"Unsupported language code '{language}'.");
            return;
        }

        var extension = format?.ToLowerInvariant() switch
        {
            "webm" => ".webm",
            "m4a" => ".m4a",
            _ => ".dat", // ffmpeg content-probes the format regardless; extension is for clarity only
        };
        var tempPath = Path.Combine(Path.GetTempPath(), $"{connId}{extension}");
        try
        {
            await File.WriteAllBytesAsync(tempPath, buffer.ToArray());
            var transcript = await _transcription.TranscribeAsync(tempPath, language);
            await Clients.Caller.SendAsync("FinalTranscript", transcript);
            _logger.LogInformation("FinalizeStream for {ConnId}: {Chars} chars", connId, transcript.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FinalizeStream failed for {ConnId}", connId);
            await Clients.Caller.SendAsync("TranscriptionError", "Transcription failed. Please try recording again.");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            buffer.Clear();
        }
    }
}
