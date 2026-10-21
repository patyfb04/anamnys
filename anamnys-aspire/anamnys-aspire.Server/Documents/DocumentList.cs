using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Documents;

// A POST body, not a query string: the patient name must never land in a URL.
public sealed record DocumentSearchRequest(string? Status, Guid? PatientId, string? Name, int? Page)
{
    public const int MaxName = 100;
    public const int MaxPage = 10_000;
    private static readonly string[] Statuses = ["draft", "signed", "superseded"];

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (Status is not null && !Statuses.Contains(Status))
        {
            errors["status"] = ["Filtro inválido."];
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

public sealed record DocumentListItem(
    Guid Id, Guid PatientId, string PatientName, string Kind, string Title, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? SignedAt, Guid? SupersededBy);

public sealed record DocumentSearchResponse(IReadOnlyList<DocumentListItem> Items, bool HasMore);

public static class DocumentList
{
    public const int PageSize = 25;

    public static async Task<DocumentSearchResponse> SearchAsync(
        AnamnysDbContext db, Guid providerId, DocumentSearchRequest request, CancellationToken ct)
    {
        var rows =
            from d in db.ClinicalDocuments.AsNoTracking()
            join p in db.Patients.AsNoTracking() on d.PatientId equals p.Id
            where d.ProviderId == providerId && p.ProviderId == providerId
            select new { d, p };
        rows = request.Status switch
        {
            "superseded" => rows.Where(r => r.d.SupersededBy != null),
            "signed" => rows.Where(r => r.d.SignedAt != null && r.d.SupersededBy == null),
            "draft" => rows.Where(r => r.d.SignedAt == null),
            _ => rows,
        };
        if (request.PatientId is { } patientId)
        {
            rows = rows.Where(r => r.d.PatientId == patientId);
        }
        if (PatientSearchQuery.ContainsPattern(request.Name) is { } name)
        {
            rows = rows.Where(r => EF.Functions.ILike(r.p.FirstName + " " + r.p.LastName, name, "\\"));
        }

        var page = request.Page ?? 1;
        var found = await rows
            .OrderByDescending(r => r.d.CreatedAt).ThenByDescending(r => r.d.Id)
            .Skip((page - 1) * PageSize)
            .Take(PageSize + 1)
            .Select(r => new
            {
                r.d.Id, r.d.PatientId, PatientName = r.p.FirstName + " " + r.p.LastName, r.d.Kind,
                r.d.CreatedAt, r.d.SignedAt, r.d.SupersededBy,
            })
            .ToListAsync(ct);
        var hasMore = found.Count > PageSize;
        var items = found.Take(PageSize)
            .Select(x => new DocumentListItem(
                x.Id, x.PatientId, x.PatientName, x.Kind, DocumentCatalog.KindTitle(x.Kind),
                x.SupersededBy is not null ? "superseded" : x.SignedAt is not null ? "signed" : "draft",
                x.CreatedAt, x.SignedAt, x.SupersededBy))
            .ToList();
        return new DocumentSearchResponse(items, hasMore);
    }
}
