namespace Anamnys.Server.Excerpts;

public readonly record struct ExcerptSpan(int Start, int End);

// Finds a quote inside a section text: case-insensitive, whitespace runs count as one space.
// Offsets are in the original text; the first occurrence wins.
public static class ExcerptLocator
{
    public static ExcerptSpan? Locate(string? sectionText, string? quote)
    {
        if (string.IsNullOrEmpty(sectionText) || string.IsNullOrWhiteSpace(quote))
        {
            return null;
        }
        var (text, map) = Normalize(sectionText);
        var (needle, _) = Normalize(quote);
        needle = needle.Trim(' ');
        if (needle.Length == 0)
        {
            return null;
        }
        var at = text.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }
        return new ExcerptSpan(map[at], map[at + needle.Length - 1] + 1);
    }

    // map[i] = index in the original text of normalized char i (a collapsed run maps to its first char).
    private static (string Text, List<int> Map) Normalize(string value)
    {
        var chars = new List<char>(value.Length);
        var map = new List<int>(value.Length);
        var previousSpace = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                if (!previousSpace)
                {
                    chars.Add(' ');
                    map.Add(i);
                }
                previousSpace = true;
                continue;
            }
            previousSpace = false;
            chars.Add(char.ToLowerInvariant(value[i]));
            map.Add(i);
        }
        return (new string(chars.ToArray()), map);
    }
}
