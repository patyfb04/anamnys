namespace Anamnys.Server.LiveSession;

// design/specs/2026-10-10-live-session-transcription-design.md §2 and §4.1.
public static class LiveSessionRules
{
    public const int SampleRate = 16000;
    public const long MaxAudioBytes = 90L * 60 * SampleRate * 2;
    public static readonly TimeSpan BufferIdleTimeout = TimeSpan.FromMinutes(10);
    public static readonly string[] Formats = ["DAP", "SOAP", "Biopsychosocial"];
    // Portuguese only while the product serves Brazil.
    public static readonly string[] Languages = ["pt"];

    public static bool IsValid(string format, string language) =>
        Formats.Contains(format, StringComparer.Ordinal) && Languages.Contains(language, StringComparer.Ordinal);
}
