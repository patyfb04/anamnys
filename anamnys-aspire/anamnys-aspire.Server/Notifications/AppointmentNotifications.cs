using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notifications;

public static class NotificationKinds
{
    public const string Confirmed = "appointment_confirmed";
    public const string AutoCancelled = "appointment_auto_cancelled";
}

// Called from Appointments (and the worker) before their single SaveChanges, so the outbox
// rows commit or roll back with the appointment change. See
// design/specs/2026-10-07-appointment-notifications-design.md §4.
public static class AppointmentNotifications
{
    public static async Task OnScheduledAsync(
        AnamnysDbContext db, Appointment appointment, bool patientHasEmail, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await CloseOpenAsync(db, appointment.Id, nowUtc, ct);
        await CancelPendingAsync(db, appointment.Id, ct);
        if (!patientHasEmail)
        {
            return;
        }

        var confirmation = new AppointmentConfirmation
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            CreatedAt = nowUtc,
            ExpiresAt = appointment.StartsAt,
        };
        db.AppointmentConfirmations.Add(confirmation);
        db.Reminders.Add(NewReminder(appointment.Id, ReminderTemplates.Confirmation, nowUtc, confirmation.Id));
        if (ConfirmationRules.ReminderAt(appointment.StartsAt, nowUtc) is { } reminderAt)
        {
            db.Reminders.Add(NewReminder(appointment.Id, ReminderTemplates.Reminder1h, reminderAt, confirmation.Id));
        }
    }

    public static async Task OnConfirmedAsync(
        AnamnysDbContext db, Appointment appointment, string by, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var open = await OpenConfirmationAsync(db, appointment.Id, ct);
        if (open is not null)
        {
            open.ConfirmedAt = nowUtc;
            open.ConfirmedBy = by;
        }
        if (by == "patient")
        {
            db.Notifications.Add(NewNotification(appointment, NotificationKinds.Confirmed, nowUtc));
        }
    }

    public static async Task OnCancelledAsync(
        AnamnysDbContext db, Appointment appointment, bool patientHasEmail, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await CloseOpenAsync(db, appointment.Id, nowUtc, ct);
        await CancelPendingAsync(db, appointment.Id, ct);
        if (patientHasEmail)
        {
            db.Reminders.Add(NewReminder(appointment.Id, ReminderTemplates.Cancellation, nowUtc, null));
        }
    }

    // Attended / no_show: nothing more to send, and the open confirmation is closed so that
    // undoing back to "scheduled" can never lead to an automatic cancellation.
    public static async Task OnLeftActiveAsync(AnamnysDbContext db, Guid appointmentId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await CloseOpenAsync(db, appointmentId, nowUtc, ct);
        await CancelPendingAsync(db, appointmentId, ct);
    }

    internal static Notification NewNotification(Appointment appointment, string kind, DateTimeOffset nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        ProviderId = appointment.ProviderId,
        Kind = kind,
        SubjectType = "appointment",
        SubjectId = appointment.Id,
        Channel = "in_app",
        ScheduledFor = nowUtc,
        SentAt = nowUtc,
        DeliveryStatus = "sent",
    };

    private static Task<AppointmentConfirmation?> OpenConfirmationAsync(AnamnysDbContext db, Guid appointmentId, CancellationToken ct) =>
        db.AppointmentConfirmations
            .Where(c => c.AppointmentId == appointmentId && c.ConfirmedAt == null && c.ClosedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

    private static async Task CloseOpenAsync(AnamnysDbContext db, Guid appointmentId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var open = await db.AppointmentConfirmations
            .Where(c => c.AppointmentId == appointmentId && c.ConfirmedAt == null && c.ClosedAt == null)
            .ToListAsync(ct);
        foreach (var confirmation in open)
        {
            confirmation.ClosedAt = nowUtc;
        }
        // A confirmed confirmation is left as it is (it cannot also be closed, see
        // AppointmentConfirmations_Closed_ck); its tokens stop working because the token lookup
        // also requires the appointment to be confirmed and to start at the confirmation's
        // ExpiresAt (Task 5).
    }

    private static async Task CancelPendingAsync(AnamnysDbContext db, Guid appointmentId, CancellationToken ct)
    {
        var pending = await db.Reminders
            .Where(r => r.AppointmentId == appointmentId && r.DeliveryStatus == "pending")
            .ToListAsync(ct);
        foreach (var reminder in pending)
        {
            reminder.DeliveryStatus = "cancelled";
        }
    }

    private static Reminder NewReminder(Guid appointmentId, string template, DateTimeOffset at, Guid? confirmationId) => new()
    {
        Id = Guid.NewGuid(),
        AppointmentId = appointmentId,
        Channel = "email",
        TemplateKey = template,
        ScheduledFor = at,
        DeliveryStatus = "pending",
        ConfirmationId = confirmationId,
    };
}
