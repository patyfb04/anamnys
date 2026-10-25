using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anamnys.Server.Excerpts;

namespace Anamnys.Server.Themes;

// GBNF for the theme output: a JSON array of at most maxItems (default MaxItems) objects
// {"code": <one of the given codes>, "quote": <string of at most MaxQuote chars>}. Duplicates are not
// expressible in GBNF without listing every permutation, so Parse removes them.
public static partial class ThemeGrammar
{
    public const int MaxItems = 5;
    public const int MaxQuote = ExcerptQueries.MaxQuoteLength;

    // The fixed text around the model's choices. Spacing is fixed (no optional whitespace), so after any
    // valid prefix the next fixed text is known (Forced) and is fed to the model without sampling.
    private const string ItemOpen = "{\"code\": \"";
    private const string QuoteOpen = ", \"quote\": \"";
    private const string ItemClose = "}";
    private const string Separator = ", ";

    // A JSON string body without control characters; the common escapes only (no \u).
    private static readonly string[] Shared =
    [
        $"qbody ::= qchar{{0,{MaxQuote}}} \"\\\"\"",
        "qchar ::= [^\"\\\\\\x00-\\x1f] | \"\\\\\" [\"\\\\/bfnrt]",
    ];

    public static string For(IReadOnlyList<string> codes, int maxItems = MaxItems)
    {
        CheckCodes(codes);
        var distinct = codes.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0 || maxItems <= 0)
        {
            return "root ::= \"[]\"";
        }

        // "[" ( item ( ", " item ( … )? )? )? "]", nested maxItems deep.
        var items = new StringBuilder();
        for (var i = 0; i < maxItems; i++)
        {
            items.Append(i == 0 ? "( item " : $"( {Gbnf(Separator)} item ");
        }
        items.Append(string.Concat(Enumerable.Repeat(")? ", maxItems)));
        return string.Join('\n', [
            $"root ::= \"[\" {items}\"]\"",
            Item("item", "code"),
            "code ::= " + string.Join(" | ", distinct.Select(c => Gbnf(c))),
            .. Shared]);
    }

    // Exactly the given codes, in order, each with a quote: the model only writes the quotes.
    public static string ForQuotes(IReadOnlyList<string> codes)
    {
        CheckCodes(codes);
        var distinct = codes.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return "root ::= \"[]\"";
        }
        var root = string.Join($" {Gbnf(Separator)} ", distinct.Select((_, i) => $"item{i}"));
        return string.Join('\n', [
            $"root ::= \"[\" {root} \"]\"",
            .. distinct.Select((c, i) => Item($"item{i}", Gbnf(c))),
            .. Shared]);
    }

    // Codes-only mode (the suggest dialog): a JSON array of at most maxItems of the given codes, as
    // before F-03. Nothing is forced in this mode; generation ends at the first ']'.
    public static string ForCodes(IReadOnlyList<string> codes, int maxItems = MaxItems)
    {
        CheckCodes(codes);
        var distinct = codes.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0 || maxItems <= 0)
        {
            return string.Join('\n', "root ::= \"[\" ws \"]\"", "ws ::= [ \\t\\n]?");
        }
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

    // Codes-only output: the complete quoted known codes, in order, once each, at most maxItems (a cut
    // array keeps what was complete), without quotes.
    public static IReadOnlyList<ThemeCodeQuote> ParseCodes(string output, IReadOnlyCollection<string> codes, int maxItems = MaxItems)
    {
        var known = codes as IReadOnlySet<string> ?? codes.ToHashSet(StringComparer.Ordinal);
        return QuotedCode().Matches(output)
            .Select(m => m.Groups[1].Value)
            .Where(known.Contains)
            .Distinct(StringComparer.Ordinal)
            .Take(maxItems)
            .Select(c => new ThemeCodeQuote(c, null))
            .ToList();
    }

    // Pick for codes-only output: excluded codes dropped after parsing, at most MaxItems.
    public static IReadOnlyList<ThemeCodeQuote> PickCodes(string output, IReadOnlyCollection<string> codes, IReadOnlyCollection<string> excludedCodes) =>
        ParseCodes(output, codes, ItemsWithExcluded(excludedCodes.Count))
            .Where(c => !excludedCodes.Contains(c.Code))
            .Take(MaxItems)
            .ToList();

    // The text that must follow output (a valid prefix of For(…, maxItems) or, with fixedCodes, of
    // ForQuotes(fixedCodes) output) whatever the model would choose; "" where the model chooses (start
    // or end of the array, a code, quote text, another item or the end). Never more than the grammar
    // admits: on anything unexpected it returns "".
    public static string Forced(string output, int maxItems, IReadOnlyList<string>? fixedCodes = null)
    {
        var max = fixedCodes?.Count ?? maxItems;
        var i = 0;

        // null: the literal is fully in output (i moves past it); else what is left of it ("" on a mismatch).
        string? Rest(string literal)
        {
            for (var k = 0; k < literal.Length; k++, i++)
            {
                if (i == output.Length)
                {
                    return literal[k..];
                }
                if (output[i] != literal[k])
                {
                    return "";
                }
            }
            return null;
        }

        if (Rest("[") is { } open)
        {
            return open;
        }
        if (max <= 0)
        {
            return Rest("]") ?? "";
        }
        for (var items = 0; ; items++)
        {
            if (fixedCodes is null && items == 0 && (i == output.Length || output[i] == ']'))
            {
                return "";
            }
            if (fixedCodes is not null)
            {
                if (Rest(ItemOpen + fixedCodes[items] + "\"" + QuoteOpen) is { } item)
                {
                    return item;
                }
            }
            else
            {
                if (Rest(ItemOpen) is { } item)
                {
                    return item;
                }
                // The code: up to its closing quote (codes have no escapes).
                while (true)
                {
                    if (i == output.Length)
                    {
                        return "";
                    }
                    if (output[i++] == '"')
                    {
                        break;
                    }
                }
                if (Rest(QuoteOpen) is { } quote)
                {
                    return quote;
                }
            }
            // The quote text: up to an unescaped closing quote.
            while (true)
            {
                if (i == output.Length)
                {
                    return "";
                }
                var c = output[i++];
                if (c == '\\')
                {
                    if (i == output.Length)
                    {
                        return "";
                    }
                    i++;
                }
                else if (c == '"')
                {
                    break;
                }
            }
            if (Rest(ItemClose) is { } close)
            {
                return close;
            }
            if (items + 1 == max || (fixedCodes is null && i < output.Length && output[i] == ']'))
            {
                return Rest("]") ?? "";
            }
            if (fixedCodes is null && i == output.Length)
            {
                return "";
            }
            if (Rest(Separator) is { } separator)
            {
                return separator;
            }
        }
    }

    // Accepts only a JSON array of at most maxItems {code, quote} objects with known codes and quotes
    // of at most MaxQuote chars; repeated codes are kept once (the first). An empty quote is null.
    public static IReadOnlyList<ThemeCodeQuote> Parse(string json, IReadOnlyCollection<string> codes, int maxItems = MaxItems)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new FormatException("Model output is not JSON.", e);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > maxItems)
            {
                throw new FormatException("Model output is not an array of at most the allowed number of themes.");
            }
            var known = codes as IReadOnlySet<string> ?? codes.ToHashSet(StringComparer.Ordinal);
            var result = new List<ThemeCodeQuote>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || item.EnumerateObject().Any(p => p.Name is not ("code" or "quote"))
                    || !item.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("quote", out var quote) || quote.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException("Model output item is not a {code, quote} object.");
                }
                var value = code.GetString()!;
                if (!known.Contains(value))
                {
                    throw new FormatException("Model output names a theme outside the given list.");
                }
                var text = quote.GetString()!;
                if (text.Length > MaxQuote)
                {
                    throw new FormatException("Model output quote is too long.");
                }
                if (result.All(r => r.Code != value))
                {
                    result.Add(new ThemeCodeQuote(value, string.IsNullOrWhiteSpace(text) ? null : text));
                }
            }
            return result;
        }
    }

    // Sampling can end generation before the grammar closes the array (e.g. "[" or a cut quote). The
    // grammar guarantees what was produced is a valid prefix, so every item whose code is complete is
    // kept (known codes only, at most maxItems), with its quote when the quote string was closed;
    // anything else yields no theme instead of an error.
    public static IReadOnlyList<ThemeCodeQuote> ParseOrRecover(string output, IReadOnlyCollection<string> codes, int maxItems = MaxItems)
    {
        try
        {
            return Parse(output, codes, maxItems);
        }
        catch (FormatException)
        {
            var known = codes as IReadOnlySet<string> ?? codes.ToHashSet(StringComparer.Ordinal);
            var result = new List<ThemeCodeQuote>();
            foreach (Match m in RecoverItem().Matches(output))
            {
                var code = m.Groups[1].Value;
                if (!known.Contains(code) || result.Any(r => r.Code == code))
                {
                    continue;
                }
                result.Add(new ThemeCodeQuote(code, m.Groups[2].Success ? Unescape(m.Groups[2].Value) : null));
                if (result.Count == maxItems)
                {
                    break;
                }
            }
            return result;
        }
    }

    // What a suggestion returns from the model output: the items of the full list it named, minus
    // the excluded ones (already tagged, or removed by the provider), at most MaxItems. The grammar
    // admits the excluded codes on purpose (see LlamaThemeSuggester), so up to MaxItems more items
    // than that are parsed.
    public static IReadOnlyList<ThemeCodeQuote> Pick(string output, IReadOnlyCollection<string> codes, IReadOnlyCollection<string> excludedCodes) =>
        ParseOrRecover(output, codes, ItemsWithExcluded(excludedCodes.Count))
            .Where(c => !excludedCodes.Contains(c.Code))
            .Take(MaxItems)
            .ToList();

    // Grammar size when excludedCount codes may be named and dropped: MaxItems plus up to MaxItems more.
    public static int ItemsWithExcluded(int excludedCount) => MaxItems + Math.Clamp(excludedCount, 0, MaxItems);

    // True once the output is a whole JSON array (the grammar then admits nothing more).
    public static bool IsComplete(string output)
    {
        var trimmed = output.TrimEnd();
        if (!trimmed.EndsWith(']'))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // {"code": <code>, "quote": <quote>} with the fixed spacing of ItemOpen / QuoteOpen / ItemClose.
    private static string Item(string name, string code) =>
        $"{name} ::= {Gbnf(ItemOpen)} {code} {Gbnf("\"" + QuoteOpen)} qbody {Gbnf(ItemClose)}";

    // A GBNF string literal for text (only '"' and '\' need escaping here).
    private static string Gbnf(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static void CheckCodes(IReadOnlyList<string> codes)
    {
        foreach (var code in codes)
        {
            if (!SafeCode().IsMatch(code))
            {
                throw new ArgumentException($"Theme code '{code}' is not a plain identifier.", nameof(codes));
            }
        }
    }

    private static string? Unescape(string raw)
    {
        try
        {
            var text = JsonSerializer.Deserialize<string>($"\"{raw}\"");
            return string.IsNullOrWhiteSpace(text) || text.Length > MaxQuote ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SafeCode();

    [GeneratedRegex("\"([a-z][a-z0-9_]*)\"")]
    private static partial Regex QuotedCode();

    // An item with a complete code, and its quote when the string was closed.
    [GeneratedRegex("\\{\\s*\"code\"\\s*:\\s*\"([a-z][a-z0-9_]*)\"(?:\\s*,\\s*\"quote\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\")?")]
    private static partial Regex RecoverItem();
}
