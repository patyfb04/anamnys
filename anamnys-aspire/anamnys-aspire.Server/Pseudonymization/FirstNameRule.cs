using System.Reflection;
using System.Text.RegularExpressions;

namespace Anamnys.Server.Pseudonymization;

// Safety net behind the model (design/specs/2026-11-17-pseudonymization-design.md §3): a capitalised word that is a common
// Brazilian first name (embedded list, first-names.txt) and not excluded is a person, also at the start of a sentence
// (over-suppression is preferred to a leak). The span runs on over the capitalised words right after it ("Joana Pereira",
// "Joana da Silva"), so the full name gets one token.
public static partial class FirstNameRule
{
    public const string ResourceName = "Anamnys.Server.Pseudonymization.first-names.txt";

    private static readonly Lazy<HashSet<string>> Names = new(Load);

    private static readonly HashSet<string> Particles = ["de", "da", "do", "dos", "das"];

    public static int Count => Names.Value.Count;

    public static bool IsFirstName(string word) => Names.Value.Contains(PseudonymTokens.Normalize(word));

    // excludedWords: normalised single words that are never a match (the patient's and the provider's names).
    public static IReadOnlyList<DetectedSpan> Find(string text, IReadOnlySet<string> excludedWords)
    {
        var words = Words(text);
        var spans = new List<DetectedSpan>();
        for (var i = 0; i < words.Count; i++)
        {
            var (start, length) = words[i];
            if (!char.IsUpper(text[start]))
            {
                continue;
            }
            var normalized = PseudonymTokens.Normalize(text.Substring(start, length));
            // A hyphenated compound ("Maria-Clara") counts by its first part.
            var head = normalized.Split('-')[0];
            if (!(Names.Value.Contains(normalized) || Names.Value.Contains(head)) || excludedWords.Contains(normalized) || excludedWords.Contains(head))
            {
                continue;
            }
            var end = ExtendRight(text, start + length, excludedWords);
            spans.Add(new DetectedSpan(start, end - start, EntityKinds.Person));
            while (i + 1 < words.Count && words[i + 1].Start < end)
            {
                i++;
            }
        }
        return spans;
    }

    // The end of a person mention that ends at `end`: the rest of a hyphenated word ("Joana" in "Joana-Maria"), then the
    // capitalised words right after it, one space apart, also through de/da/do/dos/das ("Joana Pereira", "Pedro da Costa").
    // A word after a sentence end is never adjacent (the punctuation is in between), so it is not taken. Nor is a capitalised
    // pronoun, kinship word or article (an unpunctuated transcript: "Joana Ela disse…"), nor an excluded word (the patient's
    // own name right after a third party): the mention ends before it, so the fingerprint stays the name's.
    internal static int ExtendRight(string text, int end, IReadOnlySet<string>? excludedWords = null)
    {
        while (end + 1 < text.Length && text[end] == '-' && char.IsLetter(text[end + 1]))
        {
            end = WordEnd(text, end + 1);
        }
        bool Stops(int start, int wordEnd)
        {
            var word = PseudonymTokens.Normalize(text[start..wordEnd]);
            return Pseudonymizer.KinshipWords.Contains(word) || Pseudonymizer.ArticleWords.Contains(word)
                || excludedWords?.Contains(word) == true;
        }
        while (end + 1 < text.Length && text[end] == ' ')
        {
            var next = end + 1;
            if (char.IsUpper(text[next]))
            {
                var wordEnd = WordEnd(text, next);
                if (wordEnd == next || Stops(next, wordEnd))
                {
                    break;
                }
                end = wordEnd;
                continue;
            }
            var particleEnd = WordEnd(text, next);
            if (particleEnd > next && Particles.Contains(text[next..particleEnd])
                && particleEnd + 1 < text.Length && text[particleEnd] == ' ' && char.IsUpper(text[particleEnd + 1]))
            {
                var wordEnd = WordEnd(text, particleEnd + 1);
                if (wordEnd == particleEnd + 1 || Stops(particleEnd + 1, wordEnd))
                {
                    break;
                }
                end = wordEnd;
                continue;
            }
            break;
        }
        return end;
    }

    // The end of the word that starts at `start` (letters, marks, inner apostrophes and hyphens); `start` when none starts there.
    private static int WordEnd(string text, int start)
    {
        var match = WordPattern().Match(text, start);
        return match.Success && match.Index == start ? start + match.Length : start;
    }

    // Letter runs (with combining marks, inner apostrophes and hyphens), as (start, length).
    internal static IReadOnlyList<(int Start, int Length)> Words(string text) =>
        [.. WordPattern().Matches(text).Select(m => (m.Index, m.Length))];


    private static HashSet<string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} not found.");
        using var reader = new StreamReader(stream);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var name = line.Trim();
            if (name.Length > 0 && !name.StartsWith('#'))
            {
                names.Add(PseudonymTokens.Normalize(name));
            }
        }
        return names;
    }

    [GeneratedRegex(@"\p{L}[\p{L}\p{M}]*(?:['’-]\p{L}[\p{L}\p{M}]*)*")]
    private static partial Regex WordPattern();
}
