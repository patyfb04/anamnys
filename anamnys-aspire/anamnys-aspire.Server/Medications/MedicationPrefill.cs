using System.Text.RegularExpressions;
using Anamnys.Server.DocumentFacts;

namespace Anamnys.Server.Medications;

public sealed record MedicationDraft(string? Drug, string? Dose, string? Posology, DateOnly? StartedOn, DateOnly? EndedOn);

// design/specs/2026-10-27-medication-timeline-design.md §1 and §3: a suggestion made with plain text rules from
// the fact value and the sentence it was read from; whatever cannot be told is left blank.
public static partial class MedicationPrefill
{
    private const int MaxText = 200;

    public static MedicationDraft From(string? value, string? quote, DateOnly? issueDate, DateOnly? documentDate)
    {
        var sources = new[] { value, quote }.Select(Collapse).Where(s => s.Length > 0).ToList();

        var dose = sources.Select(s => DoseOf(s)).FirstOrDefault(d => d is not null);
        var drug = sources.Select(DrugOf).FirstOrDefault(d => d is not null);
        var posology = sources.Select(PosologyOf).FirstOrDefault(p => p is not null);

        DateOnly? started = null;
        DateOnly? ended = null;
        foreach (var source in sources)
        {
            var (s, e) = DatesOf(source);
            started ??= s;
            ended ??= e;
        }

        var fallback = issueDate ?? documentDate;
        if (started is null && fallback is { } date && (ended is null || date <= ended))
        {
            started = date;
        }
        return new MedicationDraft(drug, dose, posology, started, ended);
    }

    // The same drug written differently: case, accents and spacing do not matter.
    public static string Fold(string? text)
    {
        var decomposed = Collapse(text).Normalize(System.Text.NormalizationForm.FormD);
        var letters = decomposed.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string(letters.ToArray()).ToLowerInvariant();
    }

    private static string? DoseOf(string text)
    {
        var m = DoseRegex().Match(text);
        return m.Success ? FormatDose(m) : null;
    }

    private static string FormatDose(Match m)
    {
        var unit = m.Groups[2].Value;
        return $"{m.Groups[1].Value} {(string.Equals(unit, "ui", StringComparison.OrdinalIgnoreCase) ? "UI" : unit.ToLowerInvariant())}";
    }

    private static string? DrugOf(string text)
    {
        var start = LeadingMarker().Replace(StartPhrase().Replace(AnyDate().Replace(text, ""), ""), "");
        var dose = DoseRegex().Match(start);
        var head = dose.Success ? start[..dose.Index] : start;
        var stop = HeadEnd().Match(head);
        if (stop.Success)
        {
            head = head[..stop.Index];
        }
        head = head.Trim(' ', ',', ';', ':', '-', '.', '/');
        return head.Any(char.IsLetter) ? Limit(head) : null;
    }

    private static string? PosologyOf(string text)
    {
        var dose = DoseRegex().Match(text);
        if (!dose.Success)
        {
            return null;
        }
        var rest = text[(dose.Index + dose.Length)..];
        var verb = UsageVerb().Match(rest);
        if (verb.Success)
        {
            rest = rest[(verb.Index + verb.Length)..];
        }
        var cut = RestEnd().Match(rest);
        if (cut.Success)
        {
            rest = rest[..cut.Index];
        }
        rest = rest.Trim(' ', ',', ';', ':', '-', '.', '/');
        return rest.Any(char.IsLetterOrDigit) ? Limit(rest) : null;
    }

    private static (DateOnly? Start, DateOnly? End) DatesOf(string text)
    {
        DateOnly? start = null;
        DateOnly? end = null;
        foreach (Match m in AnyDate().Matches(text))
        {
            if (FactValidator.ParseDate(m.Value) is not { } date)
            {
                continue;
            }
            var before = text[..m.Index];
            if (EndMarker().IsMatch(before))
            {
                end ??= date;
            }
            else if (StartMarker().IsMatch(before))
            {
                start ??= date;
            }
        }
        return (start, end);
    }

    private static string Limit(string text) => text.Length <= MaxText ? text : text[..MaxText].TrimEnd();

    private static string Collapse(string? value) =>
        value is null ? "" : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"(?<![\d.,])(\d+(?:[.,]\d+)?)\s*(mg|mcg|g|ml|UI)(?![\p{L}\d])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoseRegex();

    [GeneratedRegex(@"^\s*(?:\d+\s*[.)]\s+|[-•*]\s+)")]
    private static partial Regex LeadingMarker();

    [GeneratedRegex(@"[,;:(]|\s-\s|\d")]
    private static partial Regex HeadEnd();

    [GeneratedRegex(@"\b(?:tomar|usar|uso|administrar|aplicar)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsageVerb();

    [GeneratedRegex(@"\b(?:em uso desde|desde|in[ií]cio(?: em)?|iniciad[oa](?: em)?)\b\s*[:,;-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartPhrase();

    [GeneratedRegex(@"\(|\b(?:suspens\w*|interromp\w*|descontinu\w*|in[ií]cio|iniciad\w*|desde)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RestEnd();

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}[/.-]\d{1,2}[/.-](?:\d{4}|\d{2})\b|\b\d{1,2}(?:º|°|o)?\s+de\s+[A-Za-zçÇ]+\s+de\s+\d{4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnyDate();

    [GeneratedRegex(@"\b(?:in[ií]cio|iniciad[oa]|desde)\b[^\d]{0,20}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartMarker();

    [GeneratedRegex(@"\b(?:suspens\w*|interromp\w*|descontinu\w*)\b[^\d]{0,25}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndMarker();
}
