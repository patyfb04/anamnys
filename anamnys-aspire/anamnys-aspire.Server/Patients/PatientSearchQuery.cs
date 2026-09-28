using Anamnys.Server.Data;
using Anamnys.Server.Profile;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Patients;

// See design/specs/2026-09-27-patient-list-design.md §3. One data query plus one COUNT.
// Cost is bounded by one provider's rows: two index probes per patient
// (Appointments_Patient_Starts_idx, Notes_Patient_Created_idx).
public static class PatientSearchQuery
{
    private static readonly TimeZoneInfo PracticeTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static async Task<PatientSearchResponse> ExecuteAsync(
        AnamnysDbContext db,
        Guid providerId,
        DateTimeOffset now,
        PatientSearchRequest request,
        CancellationToken cancellationToken)
    {
        // Npgsql only writes offset-zero DateTimeOffset values to timestamptz.
        var nowUtc = now.ToUniversalTime();

        // Scope first, always. The subqueries repeat the provider filter as defence in depth.
        var rows = db.Patients.AsNoTracking()
            .Where(p => p.ProviderId == providerId)
            .Select(p => new PatientRow
            {
                Id = p.Id,
                FirstName = p.FirstName,
                LastName = p.LastName,
                Email = p.Email,
                LastVisit = p.LastVisit,
                NextAppointmentAt = db.Appointments
                    .Where(a => a.PatientId == p.Id && a.ProviderId == providerId
                        && a.StartsAt > nowUtc && (a.Status == "scheduled" || a.Status == "confirmed"))
                    .Min(a => (DateTimeOffset?)a.StartsAt),
                NoteGroup = db.Notes
                    .Where(n => n.PatientId == p.Id && n.ProviderId == providerId)
                    .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
                    .Select(n => n.Status == "Signed" || n.Status == "Exported" ? NoteStatusGroup.Signed : NoteStatusGroup.Pending)
                    .FirstOrDefault() ?? NoteStatusGroup.None,
            });

        rows = ApplyFilters(rows, request);

        var totalCount = await rows.CountAsync(cancellationToken);

        var items = await ApplySort(rows, request)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(r => new PatientListItem(
                r.Id, r.FirstName, r.LastName, r.Email, r.LastVisit, r.NextAppointmentAt, r.NoteGroup))
            .ToListAsync(cancellationToken);

        return new PatientSearchResponse(items, totalCount, request.Page, request.PageSize);
    }

    private static IQueryable<PatientRow> ApplyFilters(IQueryable<PatientRow> rows, PatientSearchRequest request)
    {
        if (ContainsPattern(request.Search) is { } search)
        {
            rows = rows.Where(r => EF.Functions.ILike(r.FirstName + " " + r.LastName, search, "\\")
                || (r.Email != null && EF.Functions.ILike(r.Email, search, "\\")));
        }
        if (ContainsPattern(request.Name) is { } name)
        {
            rows = rows.Where(r => EF.Functions.ILike(r.FirstName + " " + r.LastName, name, "\\"));
        }
        if (ContainsPattern(request.Email) is { } email)
        {
            rows = rows.Where(r => r.Email != null && EF.Functions.ILike(r.Email, email, "\\"));
        }
        if (request.NoteStatus is { Length: > 0 } statuses)
        {
            rows = rows.Where(r => statuses.Contains(r.NoteGroup));
        }
        if (StartOfDay(request.LastVisitFrom) is { } lastFrom)
        {
            rows = rows.Where(r => r.LastVisit >= lastFrom);
        }
        if (StartOfDay(request.LastVisitTo?.AddDays(1)) is { } lastToExclusive)
        {
            rows = rows.Where(r => r.LastVisit < lastToExclusive);
        }
        if (StartOfDay(request.NextVisitFrom) is { } nextFrom)
        {
            rows = rows.Where(r => r.NextAppointmentAt >= nextFrom);
        }
        if (StartOfDay(request.NextVisitTo?.AddDays(1)) is { } nextToExclusive)
        {
            rows = rows.Where(r => r.NextAppointmentAt < nextToExclusive);
        }
        return rows;
    }

    // Nulls last in both directions; ties broken by last name, first name, id so paging is stable.
    private static IQueryable<PatientRow> ApplySort(IQueryable<PatientRow> rows, PatientSearchRequest request)
    {
        var desc = request.SortDir == "desc";
        var ordered = (request.SortBy ?? PatientSortBy.NextVisit) switch
        {
            PatientSortBy.Name => desc
                ? rows.OrderByDescending(r => r.LastName).ThenByDescending(r => r.FirstName)
                : rows.OrderBy(r => r.LastName).ThenBy(r => r.FirstName),
            PatientSortBy.LastVisit => desc
                ? rows.OrderBy(r => r.LastVisit == null).ThenByDescending(r => r.LastVisit)
                : rows.OrderBy(r => r.LastVisit == null).ThenBy(r => r.LastVisit),
            PatientSortBy.NoteStatus => desc
                ? rows.OrderByDescending(r => r.NoteGroup == NoteStatusGroup.Pending ? 0 : r.NoteGroup == NoteStatusGroup.Signed ? 1 : 2)
                : rows.OrderBy(r => r.NoteGroup == NoteStatusGroup.Pending ? 0 : r.NoteGroup == NoteStatusGroup.Signed ? 1 : 2),
            _ => desc
                ? rows.OrderBy(r => r.NextAppointmentAt == null).ThenByDescending(r => r.NextAppointmentAt)
                : rows.OrderBy(r => r.NextAppointmentAt == null).ThenBy(r => r.NextAppointmentAt),
        };
        return ordered.ThenBy(r => r.LastName).ThenBy(r => r.FirstName).ThenBy(r => r.Id);
    }

    // Case-insensitive "contains" with the user's % _ \ taken literally.
    private static string? ContainsPattern(string? value) =>
        ProfileText.Clean(value) is { } cleaned
            ? "%" + cleaned.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"
            : null;

    // Midnight of the given day in the practice time zone, as UTC.
    private static DateTimeOffset? StartOfDay(DateOnly? day)
    {
        if (day is not { } d)
        {
            return null;
        }
        var local = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, PracticeTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    // Settable members, not a positional record: EF can only compose further Where/OrderBy
    // over a projection built with member initialisation.
    private sealed class PatientRow
    {
        public Guid Id { get; init; }
        public string FirstName { get; init; } = "";
        public string LastName { get; init; } = "";
        public string? Email { get; init; }
        public DateTimeOffset? LastVisit { get; init; }
        public DateTimeOffset? NextAppointmentAt { get; init; }
        public string NoteGroup { get; init; } = "";
    }
}
