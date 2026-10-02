using Anamnys.Server.Profile;

namespace Anamnys.Server.Patients;

// See design/specs/2026-10-01-patient-records-design.md §4. Each request validates itself;
// item inputs take a key prefix so the create request can report "diagnoses[1].description".

public sealed record CreatePatientRequest(
    string? FirstName,
    string? LastName,
    DateOnly? DateOfBirth,
    DiagnosisInput[]? Diagnoses,
    MedicationInput[]? Medications,
    string[]? TreatmentObjectives)
{
    public const int MaxItems = 50;

    public Dictionary<string, string[]> Validate(DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        PatientRules.ValidateBasics(errors, FirstName, LastName, DateOfBirth, today);

        ValidateList(errors, "diagnoses", Diagnoses, (item, prefix) => item.Validate(errors, prefix, today));
        ValidateList(errors, "medications", Medications, (item, prefix) => item.Validate(errors, prefix, today));
        ValidateList(errors, "treatmentObjectives", TreatmentObjectives,
            (item, prefix) => PatientRules.RequireText(errors, prefix.TrimEnd('.'), item, PatientRules.MaxItemText));

        return errors;
    }

    private static void ValidateList<T>(Dictionary<string, string[]> errors, string key, T[]? items, Action<T, string> validateItem)
    {
        if (items is null)
        {
            return;
        }
        if (items.Length > MaxItems)
        {
            errors[key] = [$"Informe no máximo {MaxItems} itens."];
            return;
        }
        for (var i = 0; i < items.Length; i++)
        {
            validateItem(items[i], $"{key}[{i}].");
        }
    }
}

public sealed record UpdatePatientRequest(string? FirstName, string? LastName, DateOnly? DateOfBirth)
{
    public Dictionary<string, string[]> Validate(DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        PatientRules.ValidateBasics(errors, FirstName, LastName, DateOfBirth, today);
        return errors;
    }
}

public sealed record DiagnosisInput(string? Description, string? IcdCode, DateOnly? ResolvedOn)
{
    public Dictionary<string, string[]> Validate(DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        Validate(errors, "", today);
        return errors;
    }

    internal void Validate(Dictionary<string, string[]> errors, string prefix, DateOnly today)
    {
        PatientRules.RequireText(errors, prefix + "description", Description, PatientRules.MaxItemText);
        PatientRules.MaxLength(errors, prefix + "icdCode", IcdCode, 10);
        if (ResolvedOn > today)
        {
            errors[prefix + "resolvedOn"] = ["A data não pode estar no futuro."];
        }
    }
}

public sealed record MedicationInput(string? Drug, string? Dose, string? Posology, DateOnly? StartedOn, DateOnly? EndedOn)
{
    public Dictionary<string, string[]> Validate(DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        Validate(errors, "", today);
        return errors;
    }

    internal void Validate(Dictionary<string, string[]> errors, string prefix, DateOnly today)
    {
        PatientRules.RequireText(errors, prefix + "drug", Drug, PatientRules.MaxItemText);
        PatientRules.MaxLength(errors, prefix + "dose", Dose, 200);
        PatientRules.MaxLength(errors, prefix + "posology", Posology, 200);

        if (StartedOn is not { } started)
        {
            errors[prefix + "startedOn"] = ["Informe a data de início."];
        }
        else if (started > today)
        {
            errors[prefix + "startedOn"] = ["A data não pode estar no futuro."];
        }

        if (EndedOn is { } ended && (ended > today || ended < StartedOn))
        {
            errors[prefix + "endedOn"] = ["O término deve ser entre o início e hoje."];
        }
    }
}

public sealed record ObjectiveInput(string? Description)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        PatientRules.RequireText(errors, "description", Description, PatientRules.MaxItemText);
        return errors;
    }
}

public sealed record CreatedResponse(Guid Id);

public sealed record PatientDetailResponse(
    Guid Id,
    string FirstName,
    string LastName,
    DateOnly? DateOfBirth,
    string? Email,
    string? Phone,
    bool HasPortalAccount,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? LastVisit,
    DateTimeOffset? NextAppointmentAt,
    string NoteStatus,
    bool CanDelete,
    IReadOnlyList<DiagnosisItem> Diagnoses,
    IReadOnlyList<MedicationItem> Medications,
    IReadOnlyList<ObjectiveItem> Objectives,
    IReadOnlyList<NoteSummary> RecentNotes);

public sealed record DiagnosisItem(Guid Id, string Description, string? IcdCode, DateTimeOffset RecordedAt, DateOnly? ResolvedOn);

public sealed record MedicationItem(Guid Id, string Drug, string? Dose, string? Posology, DateOnly StartedOn, DateOnly? EndedOn);

public sealed record ObjectiveItem(Guid Id, string Description, DateTimeOffset CreatedAt);

public sealed record NoteSummary(Guid Id, string Status, string Format, DateTimeOffset CreatedAt, DateTimeOffset? SignedAt);

internal static class PatientRules
{
    public const int MaxItemText = 500;
    private static readonly DateOnly EarliestBirth = new(1900, 1, 1);

    public static void ValidateBasics(
        Dictionary<string, string[]> errors, string? firstName, string? lastName, DateOnly? dateOfBirth, DateOnly today)
    {
        ProfileText.RequireName(errors, "firstName", firstName);
        ProfileText.RequireName(errors, "lastName", lastName);

        if (dateOfBirth is not { } dob)
        {
            errors["dateOfBirth"] = ["Informe a data de nascimento."];
        }
        else if (dob > today || dob < EarliestBirth)
        {
            errors["dateOfBirth"] = ["Informe uma data de nascimento válida."];
        }
    }

    public static void RequireText(Dictionary<string, string[]> errors, string key, string? value, int max)
    {
        var cleaned = ProfileText.Clean(value);
        if (cleaned is null)
        {
            errors[key] = ["Campo obrigatório."];
        }
        else if (cleaned.Length > max)
        {
            errors[key] = [$"Use no máximo {max} caracteres."];
        }
    }

    public static void MaxLength(Dictionary<string, string[]> errors, string key, string? value, int max)
    {
        if (ProfileText.Clean(value) is { } cleaned && cleaned.Length > max)
        {
            errors[key] = [$"Use no máximo {max} caracteres."];
        }
    }
}
