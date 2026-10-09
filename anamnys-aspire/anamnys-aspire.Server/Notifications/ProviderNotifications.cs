using Anamnys.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notifications;

public sealed record NotificationItem(
    Guid Id, string Kind, Guid AppointmentId, string PatientName,
    DateTimeOffset StartsAt, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record NotificationsPage(IReadOnlyList<NotificationItem> Items, int UnreadCount, int Page, int PageSize);

// The provider inbox of in-app notifications (design/specs/2026-10-07-appointment-notifications-design.md §4).
// Everything is scoped to the provider; the patient name and start come from a provider-scoped join.
public static class ProviderNotifications
{
    public const int PageSize = 20;
    private const string InApp = "in_app";

    public static async Task<NotificationsPage> ListAsync(AnamnysDbContext db, Guid providerId, int page, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        var items = await (
            from n in db.Notifications.AsNoTracking()
            join a in db.Appointments.AsNoTracking() on n.SubjectId equals a.Id
            join p in db.Patients.AsNoTracking() on a.PatientId equals p.Id
            where n.ProviderId == providerId && n.Channel == InApp && n.SubjectType == "appointment"
                && a.ProviderId == providerId && p.ProviderId == providerId
            orderby n.ScheduledFor descending, n.Id
            select new NotificationItem(n.Id, n.Kind, a.Id, p.FirstName + " " + p.LastName, a.StartsAt, n.ScheduledFor, n.ReadAt))
            .Skip((page - 1) * PageSize).Take(PageSize)
            .ToListAsync(ct);
        var unread = await db.Notifications.AsNoTracking()
            .CountAsync(n => n.ProviderId == providerId && n.Channel == InApp && n.ReadAt == null, ct);
        return new NotificationsPage(items, unread, page, PageSize);
    }

    public static async Task<bool> MarkReadAsync(AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var exists = await db.Notifications.AnyAsync(n => n.Id == id && n.ProviderId == providerId && n.Channel == InApp, ct);
        if (!exists)
        {
            return false;
        }
        await db.Notifications
            .Where(n => n.Id == id && n.ProviderId == providerId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        return true;
    }

    public static Task<int> MarkAllReadAsync(AnamnysDbContext db, Guid providerId, DateTimeOffset now, CancellationToken ct) =>
        db.Notifications
            .Where(n => n.ProviderId == providerId && n.Channel == InApp && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
}
