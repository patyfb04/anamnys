using System.Globalization;
using System.Text.RegularExpressions;

namespace Anamnys.Server.DocumentFacts;

// Format checks for the facts that have a format (design/specs/2026-10-26-document-facts-design.md §1):
// ICD X00 / X00.0, a date that exists, a registration that starts with a council acronym and carries a
// number. The other kinds only need some text. Returns the value to store (trimmed, ICD upper-cased with
// its dot) or null when it is not acceptable; the same rules apply to a provider's correction.
public static partial class FactValidator
{
    public const int MaxValueLength = 200;

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["janeiro"] = 1, ["fevereiro"] = 2, ["março"] = 3, ["marco"] = 3, ["abril"] = 4, ["maio"] = 5, ["junho"] = 6,
        ["julho"] = 7, ["agosto"] = 8, ["setembro"] = 9, ["outubro"] = 10, ["novembro"] = 11, ["dezembro"] = 12,
    };

    public static string? Normalize(string kind, string? value)
    {
        var text = Collapse(value);
        if (text.Length == 0 || text.Length > MaxValueLength)
        {
            return null;
        }
        return kind switch
        {
            FactKinds.Icd => Icd(text),
            FactKinds.IssueDate => ParseDate(text) is null ? null : text,
            FactKinds.Registration => RegistrationOf(text),
            FactKinds.Issuer or FactKinds.Medication or FactKinds.Conclusion => text.Any(char.IsLetter) ? text : null,
            _ => null,
        };
    }

    // "F43.1", "f431", "CID-10 F43.1" → "F43.1"; null when it is not one code.
    internal static string? Icd(string text)
    {
        var m = IcdCode().Match(text.ToUpperInvariant());
        if (!m.Success)
        {
            return null;
        }
        return m.Groups[3].Success ? $"{m.Groups[1].Value}{m.Groups[2].Value}.{m.Groups[3].Value}" : $"{m.Groups[1].Value}{m.Groups[2].Value}";
    }

    // The model tends to file a cited code under the conclusion it heads ("CID-10 F43.1 - Transtorno…") and
    // leave out the icd fact (seen in the probe of the sample documents). The code is literally in the
    // quoted sentence, after "CID", so each such code also becomes an icd candidate with the same page and
    // quote, unless an icd candidate already names it. Nothing is inferred: no "CID" in the quote, no code.
    public static IReadOnlyList<FactCandidate> WithCitedIcd(IReadOnlyList<FactCandidate> candidates)
    {
        var named = candidates.Where(c => c.Kind == FactKinds.Icd)
            .Select(c => Icd(Collapse(c.Value))).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var result = new List<FactCandidate>(candidates);
        foreach (var candidate in candidates.Where(c => c.Kind != FactKinds.Icd))
        {
            foreach (Match m in CitedIcd().Matches(candidate.Quote))
            {
                if (Icd(m.Groups[1].Value) is { } code && named.Add(code))
                {
                    result.Add(new FactCandidate(FactKinds.Icd, code, candidate.Page, candidate.Quote));
                }
            }
        }
        return result;
    }

    // The council registration in the text, from its acronym to its number ("Psiquiatra - CRM-SP 000000
    // (fictício)" → "CRM-SP 000000"); null when there is none or its number has fewer than 3 digits.
    internal static string? RegistrationOf(string text)
    {
        var m = Registration().Match(text);
        return m.Success && m.Value.Count(char.IsAsciiDigit) >= 3 ? m.Value.TrimEnd('.', '-', '/') : null;
    }

    // A date as Brazilian documents write it: 14/09/2026, 14-09-26, 14.09.2026, 2026-09-14,
    // "14 de setembro de 2026", "1º de março de 2026". Null when absent or not a real day.
    public static DateOnly? ParseDate(string text)
    {
        var iso = IsoDate().Match(text);
        if (iso.Success)
        {
            return Make(int.Parse(iso.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(iso.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(iso.Groups[3].Value, CultureInfo.InvariantCulture));
        }
        var numeric = NumericDate().Match(text);
        if (numeric.Success)
        {
            var year = int.Parse(numeric.Groups[3].Value, CultureInfo.InvariantCulture);
            if (numeric.Groups[3].Value.Length == 2)
            {
                year += 2000;
            }
            return Make(year, int.Parse(numeric.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(numeric.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        var written = WrittenDate().Match(text);
        if (written.Success && Months.TryGetValue(written.Groups[2].Value, out var month))
        {
            return Make(int.Parse(written.Groups[3].Value, CultureInfo.InvariantCulture), month, int.Parse(written.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        return null;
    }

    private static DateOnly? Make(int year, int month, int day) =>
        year is >= 1900 and <= 2200 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;

    private static string Collapse(string? value) =>
        value is null ? "" : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex("^(?:CID(?:-?10)?[\\s:-]*)?([A-Z])([0-9]{2})(?:\\.?([0-9]))?$")]
    private static partial Regex IcdCode();

    // "CID-10 F43.1", "CID: F40", "CID10 F33.1" inside a sentence.
    [GeneratedRegex("\\bCID(?:-?10)?\\s*[:-]?\\s*([A-Z][0-9]{2}(?:\\.[0-9])?)(?!\\.?[0-9])")]
    private static partial Regex CitedIcd();

    // A council acronym (CRM, CRP, CRFa, CRO, CREFITO, CRN, COREN, …), its optional region (-SP, -3), then the
    // number (digits with . / -, an optional letter suffix, an optional /UF).
    [GeneratedRegex("\\b(?:CR[A-Z][A-Za-z]{0,6}|COREN|COFFITO)(?:-[A-Z]{2}|-?[0-9]{1,2}(?![0-9]))?\\s*(?:n[º°o.]*\\s*)?[0-9][0-9./-]*(?:-?[A-Z]{1,2}\\b)?(?:/[A-Z]{2}\\b)?")]
    private static partial Regex Registration();

    [GeneratedRegex("\\b([0-9]{1,2})[/.-]([0-9]{1,2})[/.-]([0-9]{4}|[0-9]{2})\\b")]
    private static partial Regex NumericDate();

    [GeneratedRegex("\\b([0-9]{4})-([0-9]{2})-([0-9]{2})\\b")]
    private static partial Regex IsoDate();

    [GeneratedRegex("\\b([0-9]{1,2})(?:º|°|o)?\\s+de\\s+([A-Za-zçÇ]+)\\s+de\\s+([0-9]{4})\\b", RegexOptions.IgnoreCase)]
    private static partial Regex WrittenDate();
}
