using Anamnys.Server.Patients;

namespace Anamnys.Server.ExternalDocuments;

public static class ExternalDocumentKinds
{
    public static readonly string[] All = ["laudo", "receita", "exame", "encaminhamento", "relatorio_externo", "outro"];
}

// What the provider sends with the file. Validate before handing it to the service.
public sealed record ExternalDocumentUpload(string FileName, byte[] Content, string Kind, DateOnly? DocumentDate, string? Description)
{
    public const int MaxDescription = 200;

    public Dictionary<string, string[]> Validate(DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        if (!ExternalDocumentKinds.All.Contains(Kind))
        {
            errors["kind"] = ["Tipo de documento inválido."];
        }
        if (DocumentDate is { } date && date > today)
        {
            errors["documentDate"] = ["A data do documento não pode estar no futuro."];
        }
        if (Description is { Length: > MaxDescription })
        {
            errors["description"] = [$"Use no máximo {MaxDescription} caracteres."];
        }
        return errors;
    }

    public static DateOnly Today() => PatientSearchQuery.PracticeToday(DateTimeOffset.UtcNow);
}

// PatientName and PatientArchived are filled by the provider-wide search only (null in a patient's own list).
public sealed record ExternalDocumentItem(
    Guid Id, Guid PatientId, string? PatientName, string FileName, string Kind, DateOnly? DocumentDate,
    string? Description, string ScanStatus, long ByteSize, DateTimeOffset UploadedAt, DateTimeOffset? ScannedAt,
    bool? PatientArchived = null);

public sealed record ExternalDocumentSearchResponse(IReadOnlyList<ExternalDocumentItem> Items, bool HasMore);

// A POST body, not a query string: the patient name must never land in a URL.
public sealed record ExternalDocumentSearchRequest(string? Kind, Guid? PatientId, string? Name, int? Page)
{
    public const int MaxName = 100;
    public const int MaxPage = 10_000;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Kind is not null && !ExternalDocumentKinds.All.Contains(Kind))
        {
            errors["kind"] = ["Filtro inválido."];
        }
        if (Name is { Length: > MaxName })
        {
            errors["name"] = [$"Use no máximo {MaxName} caracteres."];
        }
        if (Page is < 1 or > MaxPage)
        {
            errors["page"] = ["Página inválida."];
        }
        return errors;
    }
}

public enum ExternalDocumentOutcome { Ok, NotFound, Archived, Empty, TooLarge, UnsupportedType, TypeMismatch, NotAvailable }

public sealed record ExternalDocumentUploadResult(ExternalDocumentOutcome Outcome, Guid? Id = null);

public sealed record ExternalDocumentOpenResult(ExternalDocumentOutcome Outcome, Uri? Url = null, DateTimeOffset? ExpiresAt = null);
