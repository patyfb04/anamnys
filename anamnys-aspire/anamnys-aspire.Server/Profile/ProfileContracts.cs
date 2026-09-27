using System.Text.RegularExpressions;

namespace Anamnys.Server.Profile;

public sealed record ProviderProfileResponse(string Email, string Name, string? CrpNumber, string? CrpRegion);

public sealed record PatientProfileResponse(
    string? Email, string FirstName, string LastName, string? Phone, DateOnly? DateOfBirth);

// Email is deliberately absent from both update requests: it is the Keycloak login and
// changes only through the UPDATE_EMAIL action (see the account self-service spec §4).
// Limits mirror the realms' declarative user profile config where one exists there.
public sealed record UpdateProviderProfileRequest(string? Name, string? CrpNumber, string? CrpRegion)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        ProfileText.RequireName(errors, "name", Name);

        var crpNumber = ProfileText.Clean(CrpNumber);
        var crpRegion = ProfileText.Clean(CrpRegion);
        if (crpNumber is not null && crpNumber.Length > 20)
        {
            errors["crpNumber"] = ["O número do CRP pode ter no máximo 20 caracteres."];
        }
        if (crpRegion is not null && crpRegion.Length > 10)
        {
            errors["crpRegion"] = ["A região do CRP pode ter no máximo 10 caracteres."];
        }

        // Providers_Crp_ck: both set or both null.
        if (crpNumber is not null && crpRegion is null)
        {
            errors.TryAdd("crpRegion", ["Informe a região do CRP."]);
        }
        if (crpRegion is not null && crpNumber is null)
        {
            errors.TryAdd("crpNumber", ["Informe o número do CRP."]);
        }

        return errors;
    }
}

public sealed record UpdatePatientProfileRequest(
    string? FirstName, string? LastName, string? Phone, DateOnly? DateOfBirth)
{
    public Dictionary<string, string[]> Validate(DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        ProfileText.RequireName(errors, "firstName", FirstName);
        ProfileText.RequireName(errors, "lastName", LastName);

        var phone = ProfileText.Clean(Phone);
        if (phone is not null && (phone.Length > 30 || !ProfileText.PhonePattern().IsMatch(phone)))
        {
            errors["phone"] = ["Use até 30 caracteres: números, espaços e + ( ) -."];
        }

        if (DateOfBirth is { } dateOfBirth && dateOfBirth > today)
        {
            errors["dateOfBirth"] = ["A data de nascimento não pode estar no futuro."];
        }

        return errors;
    }
}

public static partial class ProfileText
{
    public static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static void RequireName(Dictionary<string, string[]> errors, string key, string? value)
    {
        var cleaned = Clean(value);
        if (cleaned is null)
        {
            errors[key] = ["Campo obrigatório."];
        }
        else if (cleaned.Length > 255)
        {
            errors[key] = ["Use no máximo 255 caracteres."];
        }
    }

    [GeneratedRegex(@"^[0-9+()\- ]+$")]
    public static partial Regex PhonePattern();
}
