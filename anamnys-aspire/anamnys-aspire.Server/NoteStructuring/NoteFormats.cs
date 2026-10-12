namespace Anamnys.Server.NoteStructuring;

// Section keys per note format, in display order (design/specs/2026-10-12-note-structuring-design.md §4).
public static class NoteFormats
{
    private static readonly Dictionary<string, string[]> Keys = new(StringComparer.Ordinal)
    {
        ["DAP"] = ["data", "assessment", "plan"],
        ["SOAP"] = ["subjective", "objective", "assessment", "plan"],
        ["Biopsychosocial"] = ["biological", "psychological", "social", "plan"],
    };

    // What belongs in each section, as told to the model. The assessment is always the
    // provider's own: the model files what was dictated and never writes one (CFP).
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["data"] = "o que o paciente relatou e o que foi observado na sessão",
        ["assessment"] = "a avaliação que o(a) profissional ditou; nunca crie uma",
        ["plan"] = "próximos passos, encaminhamentos e tarefas ditados",
        ["subjective"] = "o relato do paciente, como ditado",
        ["objective"] = "observações e medidas ditadas pelo(a) profissional",
        ["biological"] = "aspectos biológicos ditados (sono, alimentação, medicação, saúde física)",
        ["psychological"] = "aspectos psicológicos ditados (emoções, pensamentos, comportamento)",
        ["social"] = "aspectos sociais ditados (família, trabalho, relações)",
    };

    public static bool IsStructurable(string format) => Keys.ContainsKey(format);

    public static IReadOnlyList<string> KeysFor(string format) =>
        Keys.TryGetValue(format, out var keys) ? keys : throw new ArgumentException($"Unknown note format '{format}'.", nameof(format));

    public static string Describe(string key) => Descriptions[key];
}
