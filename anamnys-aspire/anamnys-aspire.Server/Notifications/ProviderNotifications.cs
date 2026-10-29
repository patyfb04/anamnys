using Anamnys.Server.Data;
using Anamnys.Server.Exports;
using Anamnys.Server.Retention;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notifications;

public sealed record NotificationItem(
    Guid Id, string Kind, Guid? AppointmentId, string? PatientName,
    DateTimeOffset? StartsAt, Guid? ExportId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record NotificationsPage(IReadOnlyList<NotificationItem> Items, int UnreadCount, int Page, int PageSize);

// The provider inbox of in-app notifications (design/specs/2026-10-07-appointment-notifications-design.md §4).
// Everything is scoped to the provider; the patient name and start come from a provider-scoped join.
public static class ProviderNotifications
{
    private sealed class Row
    {
        public Guid Id { get; set; }
        public string Kind { get; set; } = "";
        public Guid? AppointmentId { get; set; }
        public string? PatientName { get; set; }
        public DateTimeOffset? StartsAt { get; set; }
        public Guid? ExportId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ReadAt { get; set; }
    }

    public const int PageSize = 20;
    private const string InApp = "in_app";

    public static async Task<NotificationsPage> ListAsync(AnamnysDbContext db, Guid providerId, int page, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        var appointmentItems =
            from n in db.Notifications.AsNoTracking()
            join a in db.Appointments.AsNoTracking() on n.SubjectId equals a.Id
            join p in db.Patients.AsNoTracking() on a.PatientId equals p.Id
            where n.ProviderId == providerId && n.Channel == InApp && n.SubjectType == "appointment"
                && a.ProviderId == providerId && p.ProviderId == providerId
            select new Row { Id = n.Id, Kind = n.Kind, AppointmentId = a.Id, PatientName = p.FirstName + " " + p.LastName, StartsAt = a.StartsAt, ExportId = null, CreatedAt = n.ScheduledFor, ReadAt = n.ReadAt };
        var exportItems =
            from n in db.Notifications.AsNoTracking()
            join e in db.AccountExports.AsNoTracking() on n.SubjectId equals e.Id
            where n.ProviderId == providerId && n.Channel == InApp && n.SubjectType == AccountExportService.SubjectType
                && e.ProviderId == providerId
            select new Row { Id = n.Id, Kind = n.Kind, AppointmentId = null, PatientName = null, StartsAt = null, ExportId = e.Id, CreatedAt = n.ScheduledFor, ReadAt = n.ReadAt };
        // Retention panel items (F-06): a patient whose deadline is near or past, an unsigned note; both open the
        // "Conformidade" page. A discarded patient's items are not listed.
        var retentionItems =
            from n in db.Notifications.AsNoTracking()
            join p in db.Patients.AsNoTracking() on n.SubjectId equals p.Id
            where n.ProviderId == providerId && n.Channel == InApp && n.SubjectType == RetentionJobSteps.PatientSubject
                && p.ProviderId == providerId && p.DiscardedAt == null
            select new Row { Id = n.Id, Kind = n.Kind, AppointmentId = null, PatientName = p.FirstName + " " + p.LastName, StartsAt = null, ExportId = null, CreatedAt = n.ScheduledFor, ReadAt = n.ReadAt };
        var noteItems =
            from n in db.Notifications.AsNoTracking()
            join note in db.Notes.AsNoTracking() on n.SubjectId equals note.Id
            join p in db.Patients.AsNoTracking() on note.PatientId equals p.Id
            where n.ProviderId == providerId && n.Channel == InApp && n.SubjectType == RetentionJobSteps.NoteSubject
                && note.ProviderId == providerId && p.ProviderId == providerId && p.DiscardedAt == null
            select new Row { Id = n.Id, Kind = n.Kind, AppointmentId = null, PatientName = p.FirstName + " " + p.LastName, StartsAt = null, ExportId = null, CreatedAt = n.ScheduledFor, ReadAt = n.ReadAt };
        var rows = await appointmentItems.Concat(exportItems).Concat(retentionItems).Concat(noteItems)
            .OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .ToListAsync(ct);
        var items = rows
            .Select(r => new NotificationItem(r.Id, r.Kind, r.AppointmentId, r.PatientName, r.StartsAt, r.ExportId, r.CreatedAt, r.ReadAt))
            .ToList();
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
