using System.Text.RegularExpressions;
using Whisper.net;

namespace Anamnys.Server.LiveSession;

public sealed partial class WhisperTranscriber(Func<CancellationToken, Task<WhisperFactory>> factory) : ITranscriber
{
    public WhisperTranscriber(WhisperModelProvider models) : this(models.GetFactoryAsync) { }

    public async Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct)
    {
        var whisper = await factory(ct);
        await using var processor = whisper.CreateBuilder().WithLanguage(language).Build();
        var segments = new List<string>();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            segments.Add(segment.Text);
        }
        return CleanSegments(segments);
    }

    // Drops a segment only when all of it is a marker; dictated text such as
    // "Sertralina (50 mg) à noite." is kept verbatim.
    internal static string CleanSegments(IEnumerable<string> segments) =>
        string.Concat(segments.Where(s => !NonSpeech().IsMatch(s))).Trim();

    // Whisper's markers for silence or noise, e.g. "[BLANK_AUDIO]", "(música)".
    [GeneratedRegex(@"^\s*[\[(][^\])]*[\])]\s*$")]
    private static partial Regex NonSpeech();
}
