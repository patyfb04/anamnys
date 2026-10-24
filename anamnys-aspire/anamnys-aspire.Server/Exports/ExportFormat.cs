using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Anamnys.Server.Exports;

// Shared formats of the export (spec 2026-10-23 §4): practice-local ISO dates, CSV for Excel, JSON.
public static class ExportFormat
{
    public static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly TimeZoneInfo Practice = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static DateTimeOffset Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Practice);

    public static DateTimeOffset? Local(DateTimeOffset? instant) => instant is { } value ? Local(value) : null;

    public static string IsoDate(DateTimeOffset instant) => Local(instant).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Iso(DateTimeOffset instant) => Local(instant).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    public static string Display(DateTimeOffset instant) => Local(instant).ToString("dd/MM/yyyy HH:mm", PtBr);

    public static string Display(DateOnly date) => date.ToString("dd/MM/yyyy", PtBr);

    public static string DisplayDay(DateTimeOffset instant) => Local(instant).ToString("dd/MM/yyyy", PtBr);

    // First letter without accents, A-Z only; X when the name has none.
    public static char Initial(string name)
    {
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            var upper = char.ToUpperInvariant(c);
            if (upper is >= 'A' and <= 'Z')
            {
                return upper;
            }
        }
        return 'X';
    }

    public static string Csv(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder("﻿");
        AppendRow(sb, header);
        foreach (var row in rows)
        {
            AppendRow(sb, row);
        }
        return sb.ToString();
    }

    private static void AppendRow<T>(StringBuilder sb, IReadOnlyList<T> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(';');
            }
            var text = CsvValue(values[i]);
            if (text.IndexOfAny([';', '"', '\r', '\n']) >= 0)
            {
                sb.Append('"').Append(text.Replace("\"", "\"\"")).Append('"');
            }
            else
            {
                sb.Append(text);
            }
        }
        sb.Append("\r\n");
    }

    // A spreadsheet would run text starting with these as a formula: an apostrophe keeps it text.
    public static string FormulaSafe(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;

    private static string CsvValue(object? value) => value switch
    {
        null => "",
        DateTimeOffset instant => Iso(instant),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool flag => flag ? "sim" : "não",
        string text => FormulaSafe(text),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
