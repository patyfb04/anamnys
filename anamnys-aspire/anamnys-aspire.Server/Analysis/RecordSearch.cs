using System.Globalization;
using System.Text;

namespace Anamnys.Server.Analysis;

public sealed record RecordSearchVocabularyTerm(Guid Id, string Code, string Label, Guid? ParentId);

// Tokens: every word of the question, lower-case and without accents. Words: the ones left to look for.
public sealed record RecordSearchQuestion(IReadOnlyList<string> Tokens, IReadOnlyList<string> Words, string Order);

// TermIds: the terms named in the question with their descendants. CoversQuestion: every word to look for belongs to a term.
public sealed record RecordSearchTermMatch(IReadOnlyCollection<Guid> TermIds, bool CoversQuestion);

// Offsets into the snippet, in UTF-16 code units: Start inclusive, End exclusive.
public sealed record RecordSearchHighlight(int Start, int End);

// The pure part of F-16 (design/specs/2026-10-31-record-search-design.md §3): reads the question and cuts the snippet.
// Evaluative words ("melhorou") are not looked for: the answer is the passages on the theme, never a statement about it.
public static class RecordSearch
{
    public const string Chronological = "chronological";
    public const string Earliest = "earliest";
    public const string Latest = "latest";
    public const int MaxSnippetLength = 300;

    // Around each match in the text Postgres returns. Control characters: no HTML ever reaches the client.
    public const string MarkStart = "\u0001";
    public const string MarkEnd = "\u0002";

    private static readonly HashSet<string> StopWords = new(
        """
        a o as os um uma uns umas ao aos de do da dos das em no na nos nas num numa nuns numas pelo pela pelos pelas por para pra pro
        com sem sobre entre ate desde durante apos antes depois e ou mas nem que se como quando onde quem qual quais quanto quanta
        quantos quantas porque pois ja mais menos muito muita muitos muitas ainda tambem so nao sim la aqui ali
        eu tu ele ela eles elas voce voces me te lhe meu minha meus minhas seu sua seus suas dele dela deles delas
        este esta estes estas esse essa esses essas isto isso aquele aquela aqueles aquelas aquilo
        e foi foram era eram ser sido sendo estar estava estavam estou esteve estiveram tem tinha tinham teve tiveram ter ha havia houve
        fez fizemos fazer vez vezes primeira ultima alguma algum algo alguem
        falamos falou falei falaram falar falando falado conversamos conversou conversei conversaram conversar
        mencionou mencionamos mencionei mencionaram comentou comentamos comentei comentaram disse dissemos dizer relatou relatamos contou contamos
        abordamos abordou tratamos tratou discutimos discutiu apareceu apareceram surgiu surgiram
        paciente sessao sessoes consulta consultas atendimento nota notas prontuario registro registros tema temas assunto assuntos
        melhorou melhoraram melhorando melhor piorou pioraram piorando pior evoluiu progrediu regrediu mudou aumentou diminuiu bem mal
        sao estao somos sou fui fomos seja sejam sera seria tive tivemos tenho temos tiver teria vai vou pode podem deve devem faz sei sabe saber lembra lembrar
        dito dita ditos ditas disseram dizendo contaram citou citamos citado citada mencionado mencionada comentado comentada abordado abordada
        tratado tratada discutido discutida registrado registrada trouxe trouxemos aconteceu aconteceram acontecido acontecendo ocorreu ocorreram existe existem
        disso nisso desse dessa deste desta nesse nessa neste nesta todo toda todos todas alguns algumas coisa coisas
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
        StringComparer.Ordinal);

    public static RecordSearchQuestion Parse(string question)
    {
        var tokens = Tokenize(question);
        var words = tokens.Where(IsWord).Distinct(StringComparer.Ordinal).ToList();
        return new RecordSearchQuestion(tokens, words, OrderOf(tokens));
    }

    // The to_tsquery text: every word, each one quoted (tokens are letters and digits only, so none can carry an operator).
    // The Portuguese stemmer does not join -ão, -ões and -ãos ("relação", "relações"), so those endings are looked for together.
    public static string TextQuery(IReadOnlyList<string> words) =>
        string.Join(" & ", words.Select(w =>
        {
            var stem = w.EndsWith("oes", StringComparison.Ordinal) ? w[..^3]
                : w.EndsWith("ao", StringComparison.Ordinal) ? w[..^2]
                : w.EndsWith("aos", StringComparison.Ordinal) ? w[..^3]
                : null;
            return stem is null ? $"'{w}'" : $"('{stem}ao' | '{stem}oes' | '{stem}aos')";
        }));

    // By the whole label, by the code, or by either side of a label "A e B"; whole words only. A term whose match sits
    // entirely inside a longer term's match is not named: "conflito no trabalho" is not the parent Trabalho.
    public static RecordSearchTermMatch MatchTerms(RecordSearchQuestion question, IReadOnlyList<RecordSearchVocabularyTerm> terms)
    {
        var found = terms
            .Select(t => (t.Id, Positions: Phrases(t).SelectMany(p => Occurrences(question.Tokens, p)).ToHashSet()))
            .Where(t => t.Positions.Count > 0)
            .ToList();
        var named = found.Where(t => !found.Any(o => t.Positions.IsProperSubsetOf(o.Positions))).Select(t => t.Id).ToList();
        var covered = found.SelectMany(t => t.Positions).ToHashSet();
        var coversQuestion = named.Count > 0
            && Enumerable.Range(0, question.Tokens.Count).All(i => covered.Contains(i) || !IsWord(question.Tokens[i]));
        return new RecordSearchTermMatch(WithDescendants(named, terms), coversQuestion);
    }

    // marked: the whole section with MarkStart/MarkEnd around each match. The sentence with the most matches, the first
    // among equals, in plain text; a sentence over MaxSnippetLength is cut around its first match.
    public static (string Snippet, IReadOnlyList<RecordSearchHighlight> Highlights) Snippet(string marked)
    {
        var (text, matches) = Unmark(marked);
        var sentences = Sentences(text);
        if (sentences.Count == 0)
        {
            return ("", []);
        }
        var (start, end) = sentences.MaxBy(s => matches.Count(m => m.Start >= s.Start && m.End <= s.End));
        var inside = matches.Where(m => m.Start >= start && m.End <= end).ToList();
        return end - start <= MaxSnippetLength ? Slice(text, start, end, "", "", inside) : Cut(text, start, end, inside);
    }

    // What a passage shows when its reference points at a whole section.
    public static string Opening(string content) => Snippet(content).Snippet;

    private static bool IsWord(string token) => !StopWords.Contains(token);

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }
        return tokens;
    }

    private static string OrderOf(List<string> tokens)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i + 1] == "vez" && tokens[i] is "primeira" or "ultima")
            {
                return tokens[i] == "primeira" ? Earliest : Latest;
            }
        }
        return Chronological;
    }

    private static IEnumerable<IReadOnlyList<string>> Phrases(RecordSearchVocabularyTerm term)
    {
        var label = Tokenize(term.Label);
        yield return label;
        yield return Tokenize(term.Code);
        var separator = label.IndexOf("e");
        if (separator > 0 && separator < label.Count - 1)
        {
            yield return label[..separator];
            yield return label[(separator + 1)..];
        }
    }

    // The positions in tokens of every occurrence of the phrase.
    private static IEnumerable<int> Occurrences(IReadOnlyList<string> tokens, IReadOnlyList<string> phrase)
    {
        for (var i = 0; phrase.Count > 0 && i + phrase.Count <= tokens.Count; i++)
        {
            if (Enumerable.Range(0, phrase.Count).All(j => tokens[i + j] == phrase[j]))
            {
                foreach (var position in Enumerable.Range(i, phrase.Count))
                {
                    yield return position;
                }
            }
        }
    }

    private static HashSet<Guid> WithDescendants(List<Guid> named, IReadOnlyList<RecordSearchVocabularyTerm> terms)
    {
        var childrenOf = terms.Where(t => t.ParentId != null).ToLookup(t => t.ParentId!.Value, t => t.Id);
        var all = named.ToHashSet();
        var frontier = new Queue<Guid>(named);
        while (frontier.TryDequeue(out var id))
        {
            foreach (var child in childrenOf[id].Where(all.Add))
            {
                frontier.Enqueue(child);
            }
        }
        return all;
    }

    private static (string Text, List<RecordSearchHighlight> Matches) Unmark(string marked)
    {
        var text = new StringBuilder(marked.Length);
        var matches = new List<RecordSearchHighlight>();
        int? open = null;
        foreach (var c in marked)
        {
            if (c == MarkStart[0])
            {
                open = text.Length;
            }
            else if (c == MarkEnd[0])
            {
                if (open is { } start)
                {
                    matches.Add(new RecordSearchHighlight(start, text.Length));
                }
                open = null;
            }
            else
            {
                text.Append(c);
            }
        }
        return (text.ToString(), matches);
    }

    // A sentence ends at a line break or at . ! ? … before a space or the end of the text. Trimmed; none is empty.
    private static List<(int Start, int End)> Sentences(string text)
    {
        var sentences = new List<(int Start, int End)>();
        void Add(int start, int end)
        {
            while (start < end && char.IsWhiteSpace(text[start]))
            {
                start++;
            }
            while (end > start && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }
            if (end > start)
            {
                sentences.Add((start, end));
            }
        }

        var from = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ends = text[i] == '\n' || (text[i] is '.' or '!' or '?' or '…' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])));
            if (ends)
            {
                Add(from, i + 1);
                from = i + 1;
            }
        }
        Add(from, text.Length);
        return sentences;
    }

    private static (string, IReadOnlyList<RecordSearchHighlight>) Cut(string text, int start, int end, List<RecordSearchHighlight> matches)
    {
        var anchor = matches.Count > 0 ? matches[0].Start : start;
        var from = Math.Clamp(anchor - MaxSnippetLength / 3, start, end - MaxSnippetLength);
        var to = from + MaxSnippetLength;
        // Whole words, unless that would drop the match.
        var space = text.IndexOf(' ', from, anchor - from);
        from = from > start && space >= 0 ? space + 1 : from;
        space = to < end ? text.LastIndexOf(' ', to - 1, to - from) : -1;
        to = space >= 0 && matches.Where(m => m.End <= to).All(m => m.End <= space) ? space : to;
        var inside = matches.Where(m => m.Start >= from && m.End <= to).ToList();
        return Slice(text, from, to, from > start ? "…" : "", to < end ? "…" : "", inside);
    }

    private static (string, IReadOnlyList<RecordSearchHighlight>) Slice(
        string text, int start, int end, string prefix, string suffix, List<RecordSearchHighlight> matches)
    {
        var shift = prefix.Length - start;
        return (prefix + text[start..end] + suffix, matches.Select(m => new RecordSearchHighlight(m.Start + shift, m.End + shift)).ToList());
    }
}
