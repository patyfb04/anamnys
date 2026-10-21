using System.Text.RegularExpressions;
using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Documents;

// Deterministic body text of a signed document: the provider-written fields in ruleset order,
// prazo de validade last. The PDF reuses the same ordering.
public static partial class DocumentBody
{
    private const string ValidityCode = "prazo_validade";
    private const string ParagraphBreak = "\n\n";

    public static IReadOnlyList<string> ProviderFieldOrder(string kind, IReadOnlyList<string> elements)
    {
        var provider = elements
            .Where(code => DocumentCatalog.Element(kind, code).Origin == ElementOrigin.Provider)
            .ToList();
        if (provider.Remove(ValidityCode))
        {
            provider.Add(ValidityCode);
        }
        return provider;
    }

    public static string Assemble(string kind, IReadOnlyList<string> elements, IReadOnlyDictionary<string, string> fields) =>
        Assemble(kind, elements, fields, [], "", "", "");

    public static string Assemble(
        string kind, IReadOnlyList<string> elements, IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<TeamMember> team, string authorName, string crp, string patientName)
    {
        if (DocumentSections.ItemKinds.Contains(kind))
        {
            var (heading, lines) = DocumentSections.Identification(kind, fields, team, authorName, crp, patientName);
            var identification = $"{heading}\n" + string.Join("\n",
                lines.Where(l => l.Value.Trim().Length > 0).Select(l => $"{l.Label}: {l.Value}"));
            return string.Join(ParagraphBreak, DocumentSections.Build(kind, elements, fields, team, authorName)
                .Select(s => $"{s.Heading}\n{s.Text}").Prepend(identification));
        }
        var order = ProviderFieldOrder(kind, elements);
        return kind is "declaracao" or "atestado"
            ? AssembleParagraphs(kind, order, fields)
            : AssembleBlocks(kind, order, fields);
    }

    private static string AssembleParagraphs(string kind, IReadOnlyList<string> order, IReadOnlyDictionary<string, string> fields)
    {
        var flatten = kind == "atestado";
        var main = order.Where(code => code != ValidityCode)
            .Select(code => Labelled(kind, fields, code, flatten))
            .Where(text => text.Length > 0);
        var validity = Labelled(kind, fields, ValidityCode, flatten);
        if (flatten)
        {
            return string.Join(" ", main.Append(validity).Where(text => text.Length > 0));
        }
        // Declaração: one paragraph per labelled element, validity last; typed line breaks stay.
        return string.Join(ParagraphBreak, main.Append(validity).Where(text => text.Length > 0));
    }

    private static string AssembleBlocks(string kind, IReadOnlyList<string> order, IReadOnlyDictionary<string, string> fields) =>
        string.Join(ParagraphBreak, order
            .Select(code => (code, text: Value(fields, code, false)))
            .Where(x => x.text.Length > 0)
            .Select(x => $"{DocumentCatalog.Element(kind, x.code).Label}\n{x.text}"));

    // "Label: value." — the period is added only when the value does not already end a sentence.
    private static string Labelled(string kind, IReadOnlyDictionary<string, string> fields, string code, bool flatten)
    {
        var text = Value(fields, code, flatten);
        if (text.Length == 0)
        {
            return "";
        }
        var terminator = text[^1] is '.' or '!' or '?' ? "" : ".";
        return $"{DocumentCatalog.Element(kind, code).Label}: {text}{terminator}";
    }

    private static string Value(IReadOnlyDictionary<string, string> fields, string code, bool flatten)
    {
        var text = fields.TryGetValue(code, out var value) ? value.Trim() : "";
        return flatten ? LineBreaks().Replace(text, " ") : text;
    }

    [GeneratedRegex(@"\s*[\r\n]+\s*")]
    private static partial Regex LineBreaks();
}
