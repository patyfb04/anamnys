namespace Anamnys.Server.Documents;

public sealed record ConformityInput(
    string Kind,
    IReadOnlyList<string> Elements,
    IReadOnlyList<string> Prohibitions,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyCollection<string> Confirmations,
    bool ProviderHasCrp,
    bool HasPracticeCity);

public sealed record ConformityResult(IReadOnlyList<string> MissingElements, IReadOnlyList<string> MissingConfirmations)
{
    public bool ElementsComplete => MissingElements.Count == 0;
    public bool ConfirmationsComplete => MissingConfirmations.Count == 0;
}

public static class DocumentConformity
{
    private static readonly HashSet<string> CrpElements =
        ["nome_psicologo", "inscricao_crp", "nome_autor_e_inscricao_crp"];

    public static ConformityResult Check(ConformityInput input)
    {
        var missingElements = input.Elements.Where(code => IsMissing(input, code)).ToList();
        var missingConfirmations = input.Prohibitions
            .Where(code => !DocumentCatalog.Prohibition(code).ByConstruction && !input.Confirmations.Contains(code))
            .ToList();
        return new ConformityResult(missingElements, missingConfirmations);
    }

    private static bool IsMissing(ConformityInput input, string code)
    {
        var info = DocumentCatalog.Element(input.Kind, code);
        return info.Origin switch
        {
            ElementOrigin.Provider => !input.Fields.TryGetValue(code, out var text) || string.IsNullOrWhiteSpace(text),
            ElementOrigin.System when CrpElements.Contains(code) => !input.ProviderHasCrp,
            ElementOrigin.System when code == "local_emissao" => !input.HasPracticeCity,
            _ => false,
        };
    }
}
