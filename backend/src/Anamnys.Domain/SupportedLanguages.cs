namespace Anamnys.Domain;

/// <summary>
/// ISO 639-1 codes accepted for Patient.PreferredLanguage and the transcription language
/// override. Keep in sync with the frontend's language picker.
/// </summary>
public static class SupportedLanguages
{
    public static readonly HashSet<string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "pt", "es", "fr", "de",
    };

    public static bool IsValid(string? code) =>
        string.IsNullOrEmpty(code) || Codes.Contains(code);
}
