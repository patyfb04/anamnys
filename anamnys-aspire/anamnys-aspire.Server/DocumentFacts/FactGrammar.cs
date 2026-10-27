using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anamnys.Server.Excerpts;

namespace Anamnys.Server.DocumentFacts;

// GBNF for the fact output (design/specs/2026-10-26-document-facts-design.md §1): a JSON array of at most
// MaxItems objects {"kind": K, "page": P, "quote": Q, "value": V} with K one of FactKinds, P one of the
// pages given to the model, Q a string of at most MaxQuote chars and V a string of at most MaxValue
// chars (an ICD value is shaped X00 or X00.0 by the grammar itself). The quote comes before the value
// so the model copies the sentence first and reads the value from it.
//
// Spacing is fixed, so after any valid prefix the fixed JSON that follows is known (Forced) and is fed
// to the model without sampling, as in ThemeGrammar. Forced never returns more than the grammar admits:
// accepting a token the grammar rejects aborts inside llama.cpp, so on anything unexpected it returns "".
public static partial class FactGrammar
{
    public const int MaxItems = 12;
    public const int MaxQuote = ExcerptQueries.MaxQuoteLength;
    public const int MaxValue = 160;

    private const string ItemOpen = "{\"kind\": \"";
    private const string PageOpen = ", \"page\": ";
    private const string QuoteOpen = ", \"quote\": \"";
    private const string ValueOpen = ", \"value\": \"";
    private const string ItemClose = "}";
    private const string Separator = ", ";

    public static string For(IReadOnlyCollection<int> pages, int maxItems = MaxItems)
    {
        var distinct = pages.Where(p => p >= 1).Distinct().Order().ToList();
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

        // The kind decides the shape of the value, so each kind has its own rest of the item.
        var kinds = string.Join(" | ", FactKinds.All.Select(k => $"{Gbnf(k)} {(k == FactKinds.Icd ? "rest-icd" : "rest-text")}"));
        return string.Join('\n',
            $"root ::= \"[\" {items}\"]\"",
            $"item ::= {Gbnf(ItemOpen)} ( {kinds} )",
            $"rest-icd ::= {Gbnf("\"" + PageOpen)} page {Gbnf(QuoteOpen)} qbody {Gbnf(ValueOpen)} icd {Gbnf("\"" + ItemClose)}",
            $"rest-text ::= {Gbnf("\"" + PageOpen)} page {Gbnf(QuoteOpen)} qbody {Gbnf(ValueOpen)} vbody {Gbnf(ItemClose)}",
            "page ::= " + string.Join(" | ", distinct.Select(p => Gbnf(p.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
            "icd ::= [A-Z] [0-9] [0-9] ( \".\" [0-9] )?",
            // JSON string bodies without control characters; the common escapes only (no \u). They
            // include the closing quote.
            $"qbody ::= char{{1,{MaxQuote}}} \"\\\"\"",
            $"vbody ::= char{{1,{MaxValue}}} \"\\\"\"",
            "char ::= [^\"\\\\\\x00-\\x1f] | \"\\\\\" [\"\\\\/bfnrt]");
    }

    // The text that must follow output (a valid prefix of For(…, maxItems) output) whatever the model
    // would choose; "" where the model chooses (start or end of the array, a kind, a page, quote or value
    // text, another item or the end).
    public static string Forced(string output, int maxItems = MaxItems)
    {
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

        // Moves past a JSON string body up to its unescaped closing quote; false while it is still open.
        bool StringBody()
        {
            while (i < output.Length)
            {
                var c = output[i++];
                if (c == '\\')
                {
                    if (i == output.Length)
                    {
                        return false;
                    }
                    i++;
                }
                else if (c == '"')
                {
                    return true;
                }
            }
            return false;
        }

        if (Rest("[") is { } open)
        {
            return open;
        }
        if (maxItems <= 0)
        {
            return Rest("]") ?? "";
        }
        for (var items = 0; ; items++)
        {
            if (items == 0 && (i == output.Length || output[i] == ']'))
            {
                return "";
            }
            if (Rest(ItemOpen) is { } item)
            {
                return item;
            }
            // The kind (no escapes), up to its closing quote.
            if (!StringBody())
            {
                return "";
            }
            if (Rest(PageOpen) is { } page)
            {
                return page;
            }
            // The page number: the model chooses when it ends.
            while (i < output.Length && char.IsAsciiDigit(output[i]))
            {
                i++;
            }
            if (i == output.Length)
            {
                return "";
            }
            if (Rest(QuoteOpen) is { } quote)
            {
                return quote;
            }
            if (!StringBody())
            {
                return "";
            }
            if (Rest(ValueOpen) is { } value)
            {
                return value;
            }
            if (!StringBody())
            {
                return "";
            }
            if (Rest(ItemClose) is { } close)
            {
                return close;
            }
            if (items + 1 == maxItems)
            {
                return Rest("]") ?? "";
            }
            if (i == output.Length || output[i] == ']')
            {
                return "";
            }
            if (Rest(Separator) is { } separator)
            {
                return separator;
            }
        }
    }

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

    // Every complete item of the output, in order, at most maxItems: the whole array or, when sampling
    // stopped early (cap reached, end of generation), the items closed before that. Unknown kinds and
    // strings that do not decode are skipped; nothing here is validated beyond the shape.
    public static IReadOnlyList<FactCandidate> Parse(string output, int maxItems = MaxItems)
    {
        var result = new List<FactCandidate>();
        foreach (Match m in CompleteItem().Matches(output))
        {
            if (result.Count == maxItems)
            {
                break;
            }
            var kind = m.Groups[1].Value;
            if (!FactKinds.All.Contains(kind)
                || !int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var page)
                || Unescape(m.Groups[3].Value) is not { } quote
                || Unescape(m.Groups[4].Value) is not { } value)
            {
                continue;
            }
            result.Add(new FactCandidate(kind, value, page, quote));
        }
        return result;
    }

    // A GBNF string literal for text (only '"' and '\' need escaping here).
    private static string Gbnf(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? Unescape(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>($"\"{raw}\"");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("\\{\"kind\": \"([a-z_]+)\", \"page\": ([0-9]{1,4}), \"quote\": \"((?:[^\"\\\\]|\\\\.)*)\", \"value\": \"((?:[^\"\\\\]|\\\\.)*)\"\\}")]
    private static partial Regex CompleteItem();
}
