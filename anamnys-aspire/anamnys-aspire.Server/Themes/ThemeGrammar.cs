using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anamnys.Server.Themes;

// GBNF that only admits a JSON array of at most maxItems (default MaxItems) strings, each one of the
// given codes. Duplicates are not expressible in GBNF without listing every permutation, so Parse
// removes them.
public static partial class ThemeGrammar
{
    public const int MaxItems = 5;

    public static string For(IReadOnlyList<string> codes, int maxItems = MaxItems)
    {
        foreach (var code in codes)
        {
            if (!SafeCode().IsMatch(code))
            {
                throw new ArgumentException($"Theme code '{code}' is not a plain identifier.", nameof(codes));
            }
        }
        var distinct = codes.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0 || maxItems <= 0)
        {
            return string.Join('\n', "root ::= \"[\" ws \"]\"", "ws ::= [ \\t\\n]?");
        }

        // "[" ws ( code ws ( "," ws code ws ( … )? )? )? "]", nested maxItems deep.
        var items = new StringBuilder();
        for (var i = 0; i < maxItems; i++)
        {
            items.Append(i == 0 ? "( code ws " : "( \",\" ws code ws ");
        }
        items.Append(string.Concat(Enumerable.Repeat(")? ", maxItems)));
        return string.Join('\n',
            $"root ::= \"[\" ws {items}\"]\"",
            "code ::= " + string.Join(" | ", distinct.Select(c => $"\"\\\"{c}\\\"\"")),
            "ws ::= [ \\t\\n]?");
    }

    // Accepts only a JSON array of at most maxItems known codes; repeated codes are kept once.
    public static IReadOnlyList<string> Parse(string json, IReadOnlyCollection<string> codes, int maxItems = MaxItems)
    {
        List<string>? values;
        try
        {
            values = JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException e)
        {
            throw new FormatException("Model output is not a JSON array of strings.", e);
        }
        if (values is null || values.Count > maxItems)
        {
            throw new FormatException("Model output is not an array of at most the allowed number of themes.");
        }
        var known = codes as IReadOnlySet<string> ?? codes.ToHashSet(StringComparer.Ordinal);
        if (values.Any(v => v is null || !known.Contains(v)))
        {
            throw new FormatException("Model output names a theme outside the given list.");
        }
        return values.Distinct(StringComparer.Ordinal).ToList();
    }

    // Sampling can end generation before the grammar closes the array (e.g. "[" or "[\"sono\", \"lu").
    // The grammar guarantees what was produced is a valid prefix, so the complete quoted codes in it
    // are kept (known codes only, at most maxItems); anything else yields no theme instead of an error.
    public static IReadOnlyList<string> ParseOrRecover(string output, IReadOnlyCollection<string> codes, int maxItems = MaxItems)
    {
        try
        {
            return Parse(output, codes, maxItems);
        }
        catch (FormatException)
        {
            var known = codes as IReadOnlySet<string> ?? codes.ToHashSet(StringComparer.Ordinal);
            return QuotedCode().Matches(output)
                .Select(m => m.Groups[1].Value)
                .Where(known.Contains)
                .Distinct(StringComparer.Ordinal)
                .Take(maxItems)
                .ToList();
        }
    }

    // What a suggestion returns from the model output: the codes of the full list it named, minus
    // the excluded ones (already tagged, or removed by the provider), at most MaxItems. The grammar
    // admits the excluded codes on purpose (see LlamaThemeSuggester), so up to MaxItems more items
    // than that are parsed.
    public static IReadOnlyList<string> Pick(string output, IReadOnlyCollection<string> codes, IReadOnlyCollection<string> excludedCodes) =>
        ParseOrRecover(output, codes, ItemsWithExcluded(excludedCodes.Count))
            .Where(c => !excludedCodes.Contains(c))
            .Take(MaxItems)
            .ToList();

    // Grammar size when excludedCount codes may be named and dropped: MaxItems plus up to MaxItems more.
    public static int ItemsWithExcluded(int excludedCount) => MaxItems + Math.Clamp(excludedCount, 0, MaxItems);

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SafeCode();

    [GeneratedRegex("\"([a-z][a-z0-9_]*)\"")]
    private static partial Regex QuotedCode();
}
