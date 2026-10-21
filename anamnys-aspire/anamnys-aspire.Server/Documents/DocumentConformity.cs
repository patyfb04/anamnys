using Anamnys.Server.Data.Entities;

namespace Anamnys.Server.Documents;

public sealed record ConformityInput(
    string Kind,
    IReadOnlyList<string> Elements,
    IReadOnlyList<string> Prohibitions,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyCollection<string> Confirmations,
    bool ProviderHasCrp,
    bool HasPracticeCity,
    IReadOnlyList<TeamMember>? Team = null);

public sealed record ConformityResult(IReadOnlyList<string> MissingElements, IReadOnlyList<string> MissingConfirmations)
{
    public bool ElementsComplete => MissingElements.Count == 0;
    public bool ConfirmationsComplete => MissingConfirmations.Count == 0;
}

public static class DocumentConformity
{
    private static readonly HashSet<string> CrpElements =
        ["nome_psicologo", "inscricao_crp", "nome_autor_e_inscricao_crp"];

    private static readonly HashSet<string> TeamAuthorElements =
        ["nomes_autores_categorias_e_registros_profissionais", "nomes_profissionais", "inscricoes_profissionais"];

    public static ConformityResult Check(ConformityInput input)
    {
        var missingElements = input.Elements.Where(code => IsMissing(input, code)).ToList();
        var missingConfirmations = input.Prohibitions
            .Where(code => !DocumentCatalog.Prohibition(input.Kind, code).ByConstruction && !input.Confirmations.Contains(code))
            .ToList();
        return new ConformityResult(missingElements, missingConfirmations);
    }

    private static bool IsMissing(ConformityInput input, string code)
    {
        var info = DocumentCatalog.Element(input.Kind, code);
        var team = input.Team ?? [];
        return info.Origin switch
        {
            ElementOrigin.Provider when code == "nome_autor_inscricao_crp_e_titulacao" => !input.ProviderHasCrp || Blank(input, code),
            ElementOrigin.Provider => Blank(input, code),
            ElementOrigin.System when CrpElements.Contains(code) => !input.ProviderHasCrp,
            ElementOrigin.System when code == "local_emissao" => !input.HasPracticeCity,
            ElementOrigin.System when TeamAuthorElements.Contains(code) => !input.ProviderHasCrp || !TeamIdentified(team),
            ElementOrigin.System when code == "analise_identificada_por_profissional_e_categoria" =>
                team.Count == 0 || team.Any(m => string.IsNullOrWhiteSpace(m.Analysis)),
            _ => false,
        };
    }

    private static bool Blank(ConformityInput input, string code) =>
        !input.Fields.TryGetValue(code, out var text) || string.IsNullOrWhiteSpace(text);

    private static bool TeamIdentified(IReadOnlyList<TeamMember> team) =>
        team.Count > 0 && team.All(m =>
            !string.IsNullOrWhiteSpace(m.Name) && !string.IsNullOrWhiteSpace(m.Category) && !string.IsNullOrWhiteSpace(m.Registry));
}
