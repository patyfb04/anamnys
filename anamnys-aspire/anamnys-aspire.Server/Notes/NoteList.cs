using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notes;

// A POST body, not a query string: the patient name must never land in a URL.
public sealed record NoteSearchRequest(string? Status, Guid? PatientId, string? Name, int? Page, int? PageSize = null, Guid? TermId = null)
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
        if (PageSize is < 1 or > NoteList.PageSize)
        {
            errors["pageSize"] = [$"O tamanho da página vai de 1 a {NoteList.PageSize}."];
        }
        return errors;
    }
}

public sealed record NoteListItem(
    Guid Id, Guid PatientId, string PatientName, string Status, string? StructureStatus, string InputMode, string Format,
    DateTimeOffset CreatedAt, DateTimeOffset? SignedAt, DateTimeOffset? AppointmentStartsAt,
    Guid? ExcerptId = null, string? ExcerptKind = null, string? Snippet = null);

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

        if (request.TermId is { } termId)
        {
            // Signed notes only; the term or any child of it; succession is not followed (separate series).
            rows = rows.Where(r => Signed.Contains(r.n.Status)
                && db.ThemeTags.Any(t => t.NoteId == r.n.Id && t.RemovedAt == null
                    && (t.TermId == termId || db.ThemeTerms.Any(c => c.Id == t.TermId && c.ParentTermId == termId))));
        }

        var page = request.Page ?? 1;
        var size = request.PageSize ?? PageSize;
        var items = await rows
            .OrderByDescending(r => r.n.CreatedAt).ThenByDescending(r => r.n.Id)
            .Skip((page - 1) * size)
            .Take(size + 1)
            .Select(r => new NoteListItem(
                r.n.Id, r.n.PatientId, r.p.FirstName + " " + r.p.LastName, r.n.Status, r.n.StructureStatus, r.n.InputMode, r.n.Format,
                r.n.CreatedAt, r.n.SignedAt,
                db.Appointments
                    .Where(a => r.n.SessionId != null && a.SessionId == r.n.SessionId)
                    .Select(a => (DateTimeOffset?)a.StartsAt)
                    .FirstOrDefault()))
            .ToListAsync(ct);
        var hasMore = items.Count > size;
        var pageItems = hasMore ? items[..size] : items;
        if (request.TermId is { } term && pageItems.Count > 0)
        {
            pageItems = await WithExcerptsAsync(db, providerId, term, pageItems, ct);
        }
        return new NoteSearchResponse(pageItems, hasMore);
    }

    // Theme search (F-03): each result points at the passage of its matching tag (the term itself before
    // a child, then the oldest) with a short snippet around it. Tags without a reference get neither.
    private static async Task<List<NoteListItem>> WithExcerptsAsync(
        AnamnysDbContext db, Guid providerId, Guid termId, List<NoteListItem> items, CancellationToken ct)
    {
        var noteIds = items.Select(i => i.Id).ToList();
        var refs = await (
            from t in db.ThemeTags.AsNoTracking()
            join term in db.ThemeTerms.AsNoTracking() on t.TermId equals term.Id
            join r in db.ExcerptRefs.AsNoTracking() on t.ExcerptRefId equals r.Id
            where noteIds.Contains(t.NoteId) && t.RemovedAt == null && r.ProviderId == providerId
                  && r.SourceType == "note" && r.SourceId == t.NoteId
                  && (t.TermId == termId || term.ParentTermId == termId)
            select new { t.NoteId, Direct = t.TermId == termId, t.CreatedAt, r.Id, r.Kind, r.SectionKey, r.OffsetStart, r.OffsetEnd })
            .ToListAsync(ct);
        var chosen = refs
            .GroupBy(r => r.NoteId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Direct).ThenBy(r => r.CreatedAt).First());
        var sectionKeys = chosen.Values.Select(r => r.SectionKey).Distinct().ToList();
        var chosenNotes = chosen.Keys.ToList();
        var sections = await db.NoteSections.AsNoTracking()
            .Where(s => chosenNotes.Contains(s.NoteId) && sectionKeys.Contains(s.Key))
            .Select(s => new { s.NoteId, s.Key, s.Content })
            .ToListAsync(ct);
        return items.Select(item =>
        {
            if (!chosen.TryGetValue(item.Id, out var r)
                || sections.FirstOrDefault(s => s.NoteId == item.Id && s.Key == r.SectionKey) is not { } section)
            {
                return item;
            }
            var exact = r.Kind == "exact";
            return item with
            {
                ExcerptId = r.Id,
                ExcerptKind = r.Kind,
                Snippet = Excerpts.ExcerptService.Snippet(section.Content, exact ? r.OffsetStart : null, exact ? r.OffsetEnd : null),
            };
        }).ToList();
    }
}
