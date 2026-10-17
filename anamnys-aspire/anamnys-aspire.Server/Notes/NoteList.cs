using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

// A POST body, not a query string: the patient name must never land in a URL.
public sealed record NoteSearchRequest(string? Status, Guid? PatientId, string? Name, int? Page)
{
    public const int MaxName = 100;
    public const int MaxPage = 10_000;
    private static readonly string[] Statuses = ["pending", "signed", "failed"];

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

public sealed record NoteListItem(
    Guid Id, Guid PatientId, string PatientName, string Status, string? StructureStatus, string InputMode, string Format,
    DateTimeOffset CreatedAt, DateTimeOffset? SignedAt, DateTimeOffset? AppointmentStartsAt);

public sealed record NoteSearchResponse(IReadOnlyList<NoteListItem> Items, bool HasMore);

public static class NoteList
{
    public const int PageSize = 25;
    private static readonly string[] Pending = ["Draft", "Processing", "ReadyForReview"];
    private static readonly string[] Signed = ["Signed", "Exported"];

    public static async Task<NoteSearchResponse> SearchAsync(AnamnysDbContext db, Guid providerId, NoteSearchRequest request, CancellationToken ct)
    {
        var rows =
            from n in db.Notes.AsNoTracking()
            join p in db.Patients.AsNoTracking() on n.PatientId equals p.Id
            where n.ProviderId == providerId && p.ProviderId == providerId
            select new { n, p };
        rows = request.Status switch
        {
            "pending" => rows.Where(r => Pending.Contains(r.n.Status) && r.n.StructureStatus != "failed"),
            "signed" => rows.Where(r => Signed.Contains(r.n.Status)),
            "failed" => rows.Where(r => r.n.Status == "Failed" || r.n.StructureStatus == "failed"),
            _ => rows,
        };
        if (request.PatientId is { } patientId)
        {
            rows = rows.Where(r => r.n.PatientId == patientId);
        }
        if (PatientSearchQuery.ContainsPattern(request.Name) is { } name)
        {
            rows = rows.Where(r => EF.Functions.ILike(r.p.FirstName + " " + r.p.LastName, name, "\\"));
        }

        var page = request.Page ?? 1;
        var items = await rows
            .OrderByDescending(r => r.n.CreatedAt).ThenByDescending(r => r.n.Id)
            .Skip((page - 1) * PageSize)
            .Take(PageSize + 1)
            .Select(r => new NoteListItem(
                r.n.Id, r.n.PatientId, r.p.FirstName + " " + r.p.LastName, r.n.Status, r.n.StructureStatus, r.n.InputMode, r.n.Format,
                r.n.CreatedAt, r.n.SignedAt,
                db.Appointments
                    .Where(a => r.n.SessionId != null && a.SessionId == r.n.SessionId)
                    .Select(a => (DateTimeOffset?)a.StartsAt)
                    .FirstOrDefault()))
            .ToListAsync(ct);
        var hasMore = items.Count > PageSize;
        return new NoteSearchResponse(hasMore ? items[..PageSize] : items, hasMore);
    }
}
