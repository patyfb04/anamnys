namespace Anamnys.Application.Interfaces;

public interface ITranscriptionService
{
    /// <summary>
    /// Transcribes audio from a file path using Whisper.net.
    /// </summary>
    /// <param name="language">
    /// ISO 639-1 code (e.g. "en", "pt") to force a specific language, or null/empty to
    /// auto-detect the spoken language per clip.
    /// </param>
    Task<string> TranscribeAsync(string audioFilePath, string? language = null, CancellationToken ct = default);
}
