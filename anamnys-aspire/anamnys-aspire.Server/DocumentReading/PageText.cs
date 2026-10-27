using System.Text;

namespace Anamnys.Server.DocumentReading;

// Page classification and the text shape that is stored (design/specs/2026-10-26-document-facts-design.md §1).
public static class PageText
{
    public static int UsefulCharacters(string text) => text.Count(char.IsLetterOrDigit);

    // A PDF page whose embedded text has fewer useful characters than the threshold is a scan.
    public static bool NeedsOcr(string text, int minUsefulCharacters) => UsefulCharacters(text) < minUsefulCharacters;

    // "\n" line breaks, no control characters (PostgreSQL text refuses NUL), no trailing spaces on a
    // line, no blank lines at either end, at most one blank line in a row.
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var blankRun = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = new string(raw.Where(c => c == '\t' || !char.IsControl(c)).ToArray()).TrimEnd();
            if (line.Length == 0)
            {
                blankRun++;
                continue;
            }
            if (builder.Length > 0)
            {
                builder.Append(blankRun > 0 ? "\n\n" : "\n");
            }
            builder.Append(line);
            blankRun = 0;
        }
        return builder.ToString();
    }
}
