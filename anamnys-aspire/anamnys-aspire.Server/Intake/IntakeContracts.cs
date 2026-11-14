using System.Text.RegularExpressions;

namespace Anamnys.Server.Intakes;

// Wire contract of design/specs/2026-11-12-structured-intake-design.md §4 (JSON camelCase). Dates are ISO
// ("yyyy-MM-dd" answers, instants with offset).

public sealed record IntakeQuestionView(
    Guid Id, string Key, string Label, string? HelpText, string AnswerType, IReadOnlyList<string>? Choices, bool Required,
    string? TargetField, bool Disabled, int Position);

public sealed record IntakeSectionView(string Key, string Title, IReadOnlyList<IntakeQuestionView> Questions);

public sealed record IntakeTemplateSummary(
    Guid Id, string Name, string Version, bool IsDefault, Guid? BasedOnTemplateId, bool Archived, bool InUse);

public sealed record IntakeTemplateList(Guid DefaultTemplateId, IReadOnlyList<IntakeTemplateSummary> Templates);

public sealed record IntakeTemplateDetail(
    Guid Id, string Name, string Version, bool IsDefault, Guid? BasedOnTemplateId, bool Archived, bool InUse,
    IReadOnlyList<IntakeSectionView> Sections);

public sealed record AdaptIntakeTemplateRequest(string? Name);

public sealed record IntakeTemplateQuestionInput(
    string? Key, string? SectionKey, string? SectionTitle, string? Label, string? HelpText, string? AnswerType,
    IReadOnlyList<string?>? Choices, bool Required, string? TargetField, bool Disabled);

public sealed partial record UpdateIntakeTemplateRequest(string? Name, IReadOnlyList<IntakeTemplateQuestionInput?>? Questions)
{
    public const int MaxQuestions = 200;
    public const int MaxName = 100;
    public const int MaxKey = 60;
    public const int MaxSectionTitle = 100;
    public const int MaxLabel = 300;
    public const int MaxHelp = 500;
    public const int MaxChoices = 30;
    public const int MaxChoice = 100;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Name is not null && (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > MaxName))
        {
            errors["name"] = [$"Informe um nome de até {MaxName} caracteres."];
        }
        if (Questions is null || Questions.Count == 0)
        {
            errors["questions"] = ["Inclua ao menos uma pergunta."];
            return errors;
        }
        if (Questions.Count > MaxQuestions)
        {
            errors["questions"] = [$"Use no máximo {MaxQuestions} perguntas."];
            return errors;
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var registration = new HashSet<string>(StringComparer.Ordinal);
        var sectionTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < Questions.Count; i++)
        {
            var q = Questions[i];
            var at = $"questions[{i}]";
            if (q is null)
            {
                errors[at] = ["Pergunta inválida."];
                continue;
            }
            if (q.Key is null || !KeyPattern().IsMatch(q.Key) || q.Key.Length > MaxKey)
            {
                errors[$"{at}.key"] = ["Use letras minúsculas, números e _ (até 60)."];
            }
            else if (!keys.Add(q.Key))
            {
                errors[$"{at}.key"] = ["Chave repetida."];
            }
            if (q.SectionKey is null || !KeyPattern().IsMatch(q.SectionKey) || q.SectionKey.Length > MaxKey)
            {
                errors[$"{at}.sectionKey"] = ["Use letras minúsculas, números e _ (até 60)."];
            }
            Text(errors, $"{at}.sectionTitle", q.SectionTitle, MaxSectionTitle, required: true);
            if (q.SectionKey is not null && !string.IsNullOrWhiteSpace(q.SectionTitle) && !errors.ContainsKey($"{at}.sectionTitle"))
            {
                var title = q.SectionTitle.Trim();
                if (sectionTitles.TryGetValue(q.SectionKey, out var first) && first != title)
                {
                    errors[$"{at}.sectionTitle"] = ["Use o mesmo título nas perguntas da mesma seção."];
                }
                sectionTitles.TryAdd(q.SectionKey, title);
            }
            Text(errors, $"{at}.label", q.Label, MaxLabel, required: true);
            Text(errors, $"{at}.helpText", q.HelpText, MaxHelp, required: false);
            if (q.AnswerType is null || !IntakeRules.AnswerTypes.Contains(q.AnswerType))
            {
                errors[$"{at}.answerType"] = ["Tipo de resposta inválido."];
            }
            else if (q.AnswerType == IntakeRules.Choice)
            {
                var choices = q.Choices ?? [];
                if (choices.Count == 0 || choices.Count > MaxChoices
                    || choices.Any(c => string.IsNullOrWhiteSpace(c) || c.Trim().Length > MaxChoice)
                    || choices.Select(c => c!.Trim()).Distinct(StringComparer.Ordinal).Count() != choices.Count)
                {
                    errors[$"{at}.choices"] = [$"Informe de 1 a {MaxChoices} opções distintas de até {MaxChoice} caracteres."];
                }
            }
            if (q.TargetField is not null)
            {
                if (!IntakeRules.TargetFields.Contains(q.TargetField))
                {
                    errors[$"{at}.targetField"] = ["Destino inválido."];
                }
                else if (q.AnswerType is not null && IntakeRules.AnswerTypeFor(q.TargetField) != q.AnswerType)
                {
                    errors[$"{at}.targetField"] = [q.TargetField == IntakeRules.DateOfBirth
                        ? "Este destino pede uma resposta do tipo data."
                        : "Este destino pede uma resposta do tipo texto."];
                }
                else if (q.TargetField.StartsWith("patient.", StringComparison.Ordinal) && !registration.Add(q.TargetField))
                {
                    errors[$"{at}.targetField"] = ["Este campo do cadastro já é destino de outra pergunta."];
                }
            }
        }
        if (errors.Count == 0 && Questions.All(q => q!.Disabled))
        {
            errors["questions"] = ["Deixe ao menos uma pergunta ativa."];
        }
        return errors;
    }

    private static void Text(Dictionary<string, string[]> errors, string key, string? value, int max, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                errors[key] = ["Campo obrigatório."];
            }
        }
        else if (value.Trim().Length > max)
        {
            errors[key] = [$"Use no máximo {max} caracteres."];
        }
    }

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex KeyPattern();
}

public sealed record IntakeSettingsRequest(Guid? DefaultTemplateId);

public sealed record IntakeSettingsResponse(Guid DefaultTemplateId);

public sealed record IntakeListItem(
    Guid Id, string Status, DateTimeOffset CreatedAt, DateTimeOffset? SignedAt, Guid? NoteId, int PendingTransfers);

public sealed record IntakeCreated(Guid Id);

public sealed record IntakeTemplateRef(Guid Id, string Name, string Version);

public sealed record IntakeAnswerView(Guid QuestionId, string Answer, DateTimeOffset UpdatedAt);

public sealed record IntakeDetail(
    Guid Id, Guid PatientId, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? SignedAt,
    Guid? NoteId, string? NoteStatus, int PendingTransfers, IntakeTemplateRef Template, IReadOnlyList<IntakeSectionView> Sections,
    IReadOnlyList<IntakeAnswerView> Answers);

public sealed record IntakeAnswerInput(Guid? QuestionId, string? Answer);

public sealed record IntakeNoteResponse(Guid NoteId);

public sealed record IntakeTransferView(
    Guid Id, Guid QuestionId, string QuestionLabel, string SectionKey, string TargetField, string Value, string? CurrentValue,
    string Status, DateTimeOffset? DecidedAt, Guid? ResultId);
