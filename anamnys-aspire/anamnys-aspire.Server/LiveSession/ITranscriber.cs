namespace Anamnys.Server.LiveSession;

// PCM samples in [-1, 1] at 16 kHz mono → plain text. In-process only (CLAUDE.md, HIPAA constraints).
public interface ITranscriber
{
    Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct);
}
