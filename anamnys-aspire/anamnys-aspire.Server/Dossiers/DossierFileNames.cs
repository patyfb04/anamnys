using System.Globalization;
using System.Text;

namespace Anamnys.Server.Dossiers;

// Paths inside the dossier ZIP (design/specs/2026-11-04-patient-dossier-design.md §4): "<base>.<ext>", then "<base>-2.<ext>",
// "<base>-3.<ext>"... when the path is taken; a suffix such as "-substituido" goes after the number. Paths are compared
// ignoring case, so the ZIP unpacks the same on Windows.
public sealed class DossierFileNames
{
    public const int MaxNameLength = 60;

    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    public string Reserve(string baseName, string extension, string suffix = "")
    {
        for (var n = 1; ; n++)
        {
            var path = $"{baseName}{(n == 1 ? "" : $"-{n}")}{suffix}.{extension}";
            if (_used.Add(path))
            {
                return path;
            }
        }
    }

    // A stored file name as ASCII letters, digits, "-" and "_" only: accents dropped, any other run of characters
    // becomes one "-", at most 60 characters. The extension is not part of it (see Extension).
    public static string Sanitize(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName.Trim());
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        var clean = Collapse(sb.ToString()).Trim('-');
        if (clean.Length > MaxNameLength)
        {
            clean = clean[..MaxNameLength].TrimEnd('-');
        }
        return clean.Length == 0 ? "arquivo" : clean;
    }

    // The stored name's extension in lowercase ASCII letters and digits; else the one of the file's type.
    public static string Extension(string fileName, string? mimeType)
    {
        var raw = Path.GetExtension(fileName.Trim()).TrimStart('.').ToLowerInvariant();
        var clean = new string(raw.Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')).ToArray());
        return clean.Length is > 0 and <= 10 ? clean : ExtensionOf(mimeType);
    }

    public static string ExtensionOf(string? mimeType) => mimeType switch
    {
        "application/pdf" => "pdf",
        "image/jpeg" => "jpg",
        "image/png" => "png",
        _ => "bin",
    };

    private static string Collapse(string text)
    {
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-", StringComparison.Ordinal);
        }
        return text;
    }
}
