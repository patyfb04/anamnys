using Anamnys.Server.Profile;

namespace Anamnys.Server.Patients;

// The patient list groups the five raw Notes.Status values into three: pending =
// Draft/Processing/ReadyForReview, signed = Signed/Exported, none = no notes.
public static class NoteStatusGroup
{
    public const string Pending = "pending";
    public const string Signed = "signed";
    public const string None = "none";
    public static readonly string[] All = [Pending, Signed, None];

    // C# twin of the CASE in PatientSearchQuery, for code that already has the raw status.
    public static string Of(string? rawStatus) => rawStatus switch
    {
        null => None,
        "Signed" or "Exported" => Signed,
        _ => Pending,
    };
}

public static class PatientSortBy
{
    public const string Name = "name";
    public const string LastVisit = "lastVisit";
    public const string NextVisit = "nextVisit";
    public const string NoteStatus = "noteStatus";
    public static readonly string[] All = [Name, LastVisit, NextVisit, NoteStatus];
}

// Every field is optional. Filters travel in a POST body, never a query string: name and
// email are PHI and query strings end up in proxy and server logs.
public sealed record PatientSearchRequest
{
    public const int MaxTextLength = 200;
    public const int MaxPageSize = 100;

    public string? Search { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string[]? NoteStatus { get; init; }
    public DateOnly? LastVisitFrom { get; init; }
    public DateOnly? LastVisitTo { get; init; }
    public DateOnly? NextVisitFrom { get; init; }
    public DateOnly? NextVisitTo { get; init; }
    // false: active patients only; true: archived patients only.
    public bool Archived { get; init; }
    public string? SortBy { get; init; } = PatientSortBy.NextVisit;
    public string? SortDir { get; init; } = "asc";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        RequireMaxLength(errors, "search", Search);
        RequireMaxLength(errors, "name", Name);
        RequireMaxLength(errors, "email", Email);

        if (NoteStatus is not null && NoteStatus.Any(s => !NoteStatusGroup.All.Contains(s)))
        {
            errors["noteStatus"] = ["Use pending, signed ou none."];
        }
        if (SortBy is not null && !PatientSortBy.All.Contains(SortBy))
        {
            errors["sortBy"] = ["Use name, lastVisit, nextVisit ou noteStatus."];
        }
        if (SortDir is not null && SortDir is not ("asc" or "desc"))
        {
            errors["sortDir"] = ["Use asc ou desc."];
        }

        // Lifted comparison: false when either side is null, so open-ended ranges pass.
        if (LastVisitFrom > LastVisitTo)
        {
            errors["lastVisitTo"] = ["A data final deve ser igual ou posterior à inicial."];
        }
        if (NextVisitFrom > NextVisitTo)
        {
            errors["nextVisitTo"] = ["A data final deve ser igual ou posterior à inicial."];
        }

        if (Page < 1)
        {
            errors["page"] = ["A página deve ser 1 ou maior."];
        }
        if (PageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Use de 1 a {MaxPageSize} itens por página."];
        }

        return errors;
    }

    private static void RequireMaxLength(Dictionary<string, string[]> errors, string key, string? value)
    {
        if (ProfileText.Clean(value) is { Length: > MaxTextLength })
        {
            errors[key] = [$"Use no máximo {MaxTextLength} caracteres."];
        }
    }
}

public sealed record PatientListItem(
    Guid Id,
    string FirstName,
    string LastName,
    string? Email,
    DateTimeOffset? LastVisit,
    DateTimeOffset? NextAppointmentAt,
    string NoteStatus);

public sealed record PatientSearchResponse(IReadOnlyList<PatientListItem> Items, int TotalCount, int Page, int PageSize);
