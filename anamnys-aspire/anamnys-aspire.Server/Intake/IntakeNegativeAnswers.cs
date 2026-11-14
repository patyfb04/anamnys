using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Anamnys.Server.Intakes;

// Free-text answers such as "Nada fora do normal." or "Não toma medicação" say that there is nothing to record, so signing
// does not offer them as a diagnosis or a medication. The answer is trimmed, lowercased, stripped of accents and of
// trailing punctuation; it is negative when it equals, or starts with, one of the words below as a whole word ("nadador"
// is not "nada"). Applied only to the diagnosis and medication destinations (IsOfferable); everything else is offered.
public static partial class IntakeNegativeAnswers
{
    // A bare "na" (typed for "N/A") counts only as the whole answer: "Na infância tomava X" is a real answer.
    [GeneratedRegex(@"^(?:(nada|nenhum|nenhuma|nao|nega|sem|n/a|-)(?![\p{L}\p{N}])|na$)", RegexOptions.CultureInvariant)]
    private static partial Regex NegativeStart();

    public static bool IsNegative(string? answer)
    {
        var text = Normalize(answer);
        return text.Length == 0 || NegativeStart().IsMatch(text);
    }

    public static bool IsOfferable(string targetField, string answer) =>
        targetField is not (IntakeRules.Diagnosis or IntakeRules.Medication) || !IsNegative(answer);

    private static string Normalize(string? answer)
    {
        var decomposed = (answer ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }
        var text = builder.ToString().Normalize(NormalizationForm.FormC).TrimEnd();
        // "-" alone is itself the negative marker, so trailing punctuation never strips a leading dash.
        var trimmed = text.TrimEnd('.', ',', ';', ':', '!', '?', ' ');
        return trimmed.Length == 0 && text.StartsWith('-') ? "-" : trimmed;
    }
}
