using System.Text;
using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Pseudonymization;

// Counts: occurrences replaced per kind (all four kinds, zero included).
public sealed record PseudonymizationResult(string Text, IReadOnlyDictionary<string, int> CountsByKind)
{
    // The audit value: kinds and counts only, never a name.
    public string AuditValue => string.Join(",", EntityKinds.All.Select(k => $"{k}={CountsByKind.GetValueOrDefault(k)}"));
}

// Replaces third parties with per-patient tokens (design/specs/2026-11-17-pseudonymization-design.md §2–§4): detect (per
// chunk of at most ChunkChars characters, each name then located in the whole text) → exclusions → first-name safety net →
// overlaps (longest first) → tokens → replace. The text goes nowhere but the returned result; failures surface as
// exceptions that never carry it.
public sealed class Pseudonymizer(IThirdPartyDetector detector, PatientSecrets secrets, TimeProvider clock)
{
    // Kinship, role and pronoun words: never a name on their own, and dropped from the front of a person mention
    // ("tia Maria" keeps "tia" and tokenises "Maria").
    internal static readonly HashSet<string> KinshipWords =
    [
        "mae", "pai", "irmao", "irma", "filho", "filha", "avo", "tio", "tia", "primo", "prima", "marido", "esposa", "esposo",
        "namorado", "namorada", "noivo", "noiva", "companheiro", "companheira", "sogro", "sogra", "cunhado", "cunhada",
        "padrasto", "madrasta", "enteado", "enteada", "neto", "neta", "sobrinho", "sobrinha", "bisavo", "padrinho", "madrinha",
        "chefe", "patrao", "patroa", "amigo", "amiga", "colega", "vizinho", "vizinha", "professor", "professora",
        "eu", "ele", "ela", "eles", "elas", "voce", "voces", "nos", "a gente", "dele", "dela",
    ];

    // Titles before a name, dropped like kinship words ("Dr. Paulo" tokenises "Paulo").
    internal static readonly HashSet<string> TitleWords = ["dr", "dra", "doutor", "doutora", "sr", "sra", "senhor", "senhora", "seu", "dona", "d", "prof", "profa"];

    // Articles and contractions before a name ("o Pedro", "da Joana", "pro Zé"): dropped, so the fingerprint is the name's.
    internal static readonly HashSet<string> ArticleWords =
        ["o", "a", "os", "as", "do", "da", "dos", "das", "no", "na", "nos", "nas", "pro", "pra", "pros", "pras", "ao", "aos", "de", "em", "com"];

    // Characters per detector call: a long transcript is many calls (each model run has its own time budget).
    public int ChunkChars { get; init; } = 6000;

    // Characters each chunk repeats from the end of the one before, so a name cut in two at a chunk boundary ("Colégio |
    // Objetivo") is seen whole by the next call. Names found twice in the overlap are one name (located once below).
    public int OverlapChars { get; init; } = 200;

    public async Task<PseudonymizationResult> PseudonymizeAsync(AnamnysDbContext db, Guid providerId, Guid patientId, string text, CancellationToken ct)
    {
        var names = await db.Patients.AsNoTracking()
            .Where(p => p.Id == patientId && p.ProviderId == providerId)
            .Select(p => new { p.FirstName, p.LastName, ProviderName = db.Providers.Where(v => v.Id == providerId).Select(v => v.Name).FirstOrDefault() })
            .SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The note's patient was not found for the provider.");
        var exclusions = Exclusions(names.FirstName, names.LastName, names.ProviderName);
        // Single words never tokenised: the patient's names only (the provider is excluded by full name, so a third party
        // who shares the provider's first name is still replaced).
        var excludedWords = Exclusions(names.FirstName, names.LastName, null)
            .SelectMany(e => FirstNameRule.Words(e).Select(w => PseudonymTokens.Normalize(e.Substring(w.Start, w.Length))))
            .Where(w => !TitleWords.Contains(w) && !ArticleWords.Contains(w))
            .ToHashSet(StringComparer.Ordinal);
        var excludedPhrases = exclusions.Select(PseudonymTokens.Normalize).ToHashSet(StringComparer.Ordinal);

        // Each chunk's names, located again in the whole text, so a name found in one chunk is replaced everywhere.
        var found = new List<(string Text, string Kind)>();
        foreach (var (start, length) in Segments(text, ChunkChars, OverlapChars))
        {
            var chunk = text.Substring(start, length);
            foreach (var span in await detector.DetectAsync(chunk, exclusions, ct))
            {
                if (span.Start >= 0 && span.Length > 0 && span.End <= chunk.Length && EntityKinds.IsKnown(span.Kind))
                {
                    found.Add((chunk.Substring(span.Start, span.Length), span.Kind));
                }
            }
        }
        // A name in the overlap of two chunks comes back twice: one (text, kind) pair, located once.
        var candidates = LlamaThirdPartyDetector.Locate(text, [.. found.Distinct()], exclusions).Distinct().ToList();
        // The safety net joins the pool: where it covers more than the detector (the surname after a detected first
        // name), the longest span wins below.
        candidates.AddRange(FirstNameRule.Find(text, excludedWords));
        var spans = candidates.Select(s => Keep(text, s, excludedWords, excludedPhrases)).OfType<DetectedSpan>();
        var accepted = ResolveOverlaps(spans);

        var counts = EntityKinds.All.ToDictionary(k => k, _ => 0);
        if (accepted.Count == 0)
        {
            return new PseudonymizationResult(text, counts);
        }
        var numbers = await PseudonymTokens.NumbersAsync(db, secrets, patientId,
            [.. accepted.Select(s => (s.Kind, text.Substring(s.Start, s.Length)))], clock.GetUtcNow(), ct);
        var result = new StringBuilder(text.Length);
        var at = 0;
        for (var i = 0; i < accepted.Count; i++)
        {
            var span = accepted[i];
            result.Append(text, at, span.Start - at).Append(PseudonymTokens.Label(span.Kind, numbers[i]));
            at = span.End;
            counts[span.Kind]++;
        }
        result.Append(text, at, text.Length - at);
        return new PseudonymizationResult(result.ToString(), counts);
    }

    // The patient's first and last names, full name and last-name parts, and the provider's full profile name without
    // titles (never the provider's first name alone).
    internal static IReadOnlyCollection<string> Exclusions(string firstName, string lastName, string? providerName)
    {
        var list = new List<string>();
        void Add(string? value)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(trimmed);
            }
        }
        Add(firstName);
        Add(lastName);
        Add($"{firstName} {lastName}");
        foreach (var part in (lastName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length > 2))
        {
            Add(part);
        }
        if (providerName is { } provider)
        {
            var words = FirstNameRule.Words(provider).Select(w => provider.Substring(w.Start, w.Length))
                .Where(w => !TitleWords.Contains(PseudonymTokens.Normalize(w))).ToList();
            Add(string.Join(' ', words));
        }
        return list;
    }

    // Cuts the text into pieces of at most maxChars, at a sentence end when there is one, else at a space. With an overlap,
    // each piece after the first starts up to `overlap` characters before the end of the previous one, at a word start
    // (and never before its middle, so every piece moves on).
    internal static IReadOnlyList<(int Start, int Length)> Segments(string text, int maxChars, int overlap = 0)
    {
        var segments = new List<(int, int)>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + maxChars);
            if (end < text.Length)
            {
                var cut = -1;
                for (var i = end; i > start + maxChars / 2 && cut < 0; i--)
                {
                    if (char.IsWhiteSpace(text[i]) && ".!?…\n".Contains(text[i - 1]))
                    {
                        cut = i;
                    }
                }
                for (var i = end; i > start && cut < 0; i--)
                {
                    if (char.IsWhiteSpace(text[i]))
                    {
                        cut = i;
                    }
                }
                end = cut > start ? cut : end;
            }
            segments.Add((start, end - start));
            if (end >= text.Length || overlap <= 0)
            {
                start = end;
                continue;
            }
            // The next piece starts at the first word start at or after end - overlap, not before this piece's middle.
            var next = Math.Max(start + (end - start) / 2 + 1, end - overlap);
            while (next < end && !(char.IsWhiteSpace(text[next - 1]) && !char.IsWhiteSpace(text[next])))
            {
                next++;
            }
            start = next;
        }
        return segments;
    }

    // A candidate span as it is replaced: a person loses leading kinship, title and article words and runs on over the
    // capitalised words right after it ("Joana" → "Joana Pereira"; not over a pronoun, kinship word, article or excluded
    // word); null when only excluded, kinship or article words
    // remain, or the whole mention is an excluded name.
    private static DetectedSpan? Keep(string text, DetectedSpan span, IReadOnlySet<string> excludedWords, IReadOnlySet<string> excludedPhrases)
    {
        if (span.Start < 0 || span.Length <= 0 || span.End > text.Length || !EntityKinds.IsKnown(span.Kind))
        {
            return null;
        }
        var surface = text.Substring(span.Start, span.Length);
        if (KinshipWords.Contains(PseudonymTokens.Normalize(surface)))
        {
            return null;
        }
        var words = FirstNameRule.Words(surface);
        if (words.Count == 0)
        {
            return null;
        }
        var normalized = words.Select(w => PseudonymTokens.Normalize(surface.Substring(w.Start, w.Length))).ToList();
        if (span.Kind != EntityKinds.Person)
        {
            return excludedPhrases.Contains(PseudonymTokens.Normalize(surface)) || normalized.All(excludedWords.Contains) ? null : span;
        }
        var first = 0;
        while (first < words.Count
            && (KinshipWords.Contains(normalized[first]) || TitleWords.Contains(normalized[first]) || ArticleWords.Contains(normalized[first])))
        {
            first++;
        }
        if (first == words.Count)
        {
            return null;
        }
        var start = span.Start + words[first].Start;
        var end = FirstNameRule.ExtendRight(text, span.End, excludedWords);
        var kept = text[start..end];
        var keptWords = FirstNameRule.Words(kept).Select(w => PseudonymTokens.Normalize(kept.Substring(w.Start, w.Length))).ToList();
        if (excludedPhrases.Contains(PseudonymTokens.Normalize(kept)) || keptWords.All(excludedWords.Contains))
        {
            return null;
        }
        return new DetectedSpan(start, end - start, span.Kind);
    }

    // Longest first, then earliest; the kept spans in text order.
    internal static IReadOnlyList<DetectedSpan> ResolveOverlaps(IEnumerable<DetectedSpan> spans)
    {
        var accepted = new List<DetectedSpan>();
        foreach (var span in spans.OrderByDescending(s => s.Length).ThenBy(s => s.Start))
        {
            if (!accepted.Any(a => a.Start < span.End && span.Start < a.End))
            {
                accepted.Add(span);
            }
        }
        return [.. accepted.OrderBy(s => s.Start)];
    }
}
