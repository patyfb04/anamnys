using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.PatientPortal;

public enum SessionScope { Upcoming, Past }

// Only schedule fields ever leave the server through the portal: the portal never reaches
// the clinical record (design/specs/2026-10-04-patient-portal-sessions-design.md).
public sealed record PortalSession(Guid Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Timezone, string Modality, string Status);

public sealed record PortalProvider(Guid ProviderId, string Name, string? Crp, PortalSession? NextSession);

public sealed record PortalSessionsPage(IReadOnlyList<PortalSession> Items, int TotalCount, int Page, int PageSize);

// What a patient-portal account sees. Every query starts from the records linked to the
// account (Patients.AccountId); only Patients, Providers and Appointments are read.
public static class PatientPortalQueries
{
    public const int DefaultPageSize = 20;

    public static async Task<IReadOnlyList<PortalProvider>> ProvidersAsync(
        AnamnysDbContext db, Guid accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nowUtc = now.ToUniversalTime();
        var rows = await db.Patients.AsNoTracking()
            .Where(p => p.AccountId == accountId)
            .Join(db.Providers, p => p.ProviderId, pr => pr.Id, (p, pr) => new
            {
                pr.Id,
                pr.Name,
                pr.CrpNumber,
                pr.CrpRegion,
                Next = db.Appointments
                    .Where(a => a.PatientId == p.Id && a.ProviderId == p.ProviderId
                        && a.StartsAt > nowUtc && (a.Status == "scheduled" || a.Status == "confirmed"))
                    .OrderBy(a => a.StartsAt)
                    .Select(a => new PortalSession(a.Id, a.StartsAt, a.EndsAt, a.Timezone, a.Modality, a.Status))
                    .FirstOrDefault(),
            })
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new PortalProvider(r.Id, r.Name, r.CrpNumber is null ? null : $"{r.CrpNumber} {r.CrpRegion}", r.Next))
            .ToList();
    }

    // Null when no record of that provider is linked to the account: the caller answers 404
    // either way, so a provider id reveals nothing.
    public static async Task<PortalSessionsPage?> SessionsAsync(
        AnamnysDbContext db,
        Guid accountId,
        Guid providerId,
        SessionScope scope,
        int page,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        int pageSize = DefaultPageSize)
    {
        var record = await db.Patients.AsNoTracking()
            .Where(p => p.AccountId == accountId && p.ProviderId == providerId)
            .Select(p => (Guid?)p.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (record is not { } patientId)
        {
            return null;
        }

        var nowUtc = now.ToUniversalTime();
        var appointments = db.Appointments.AsNoTracking().Where(a => a.PatientId == patientId && a.ProviderId == providerId);
        var upcoming = appointments.Where(a => a.StartsAt > nowUtc && (a.Status == "scheduled" || a.Status == "confirmed"));
        var selected = scope == SessionScope.Upcoming
            ? upcoming.OrderBy(a => a.StartsAt)
            : appointments.Where(a => !(a.StartsAt > nowUtc && (a.Status == "scheduled" || a.Status == "confirmed")))
                .OrderByDescending(a => a.StartsAt);

        var total = await selected.CountAsync(cancellationToken);
        var items = await selected
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new PortalSession(a.Id, a.StartsAt, a.EndsAt, a.Timezone, a.Modality, a.Status))
            .ToListAsync(cancellationToken);

        return new PortalSessionsPage(items, total, page, pageSize);
    }
}
