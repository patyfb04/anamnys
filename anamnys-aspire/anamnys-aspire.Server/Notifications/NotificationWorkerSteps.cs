using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Email;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notifications;

// The worker's two steps (design/specs/2026-10-07-appointment-notifications-design.md §4).
// Cancellation runs in one transaction; sending uses one per reminder. Rows are locked with FOR UPDATE SKIP LOCKED.
// providerId narrows a run to one provider (tests); production passes null.
public static class NotificationWorkerSteps
{
    private const int BatchSize = 50;

    public static async Task<int> CancelOverdueAsync(
        AnamnysDbContext db, DateTimeOffset now, CancellationToken ct, Guid? providerId = null)
    {
        var nowUtc = now.ToUniversalTime();
        return await db.InTransactionAsync(async ct => await CancelOverdueInTransactionAsync(db, nowUtc, providerId, ct), ct);
    }

    private static async Task<int> CancelOverdueInTransactionAsync(
        AnamnysDbContext db, DateTimeOffset nowUtc, Guid? providerId, CancellationToken ct)
    {
        var due = await db.AppointmentConfirmations.FromSql($"""
            SELECT c.* FROM "AppointmentConfirmations" c
            JOIN "Appointments" a ON a."Id" = c."AppointmentId"
            WHERE c."ConfirmedAt" IS NULL AND c."ClosedAt" IS NULL AND c."DeadlineAt" <= {nowUtc}
              AND a."Status" = 'scheduled' AND a."StartsAt" > {nowUtc}
              AND (CAST({providerId} AS uuid) IS NULL OR a."ProviderId" = CAST({providerId} AS uuid))
            ORDER BY c."DeadlineAt"
            LIMIT {BatchSize}
            FOR UPDATE OF c, a SKIP LOCKED
            """).ToListAsync(ct);

        foreach (var confirmation in due)
        {
            var appointment = await db.Appointments.SingleAsync(a => a.Id == confirmation.AppointmentId, ct);
            var hasEmail = await db.Patients
                .Where(p => p.Id == appointment.PatientId && p.ProviderId == appointment.ProviderId)
                .Select(p => p.ContactEmail != null)
                .SingleAsync(ct);
            appointment.Status = AppointmentStatus.Cancelled;
            appointment.CancelledAt = nowUtc;
            appointment.CancelledBy = "system";
            appointment.CancellationReason = ConfirmationRules.SystemCancellationReason;
            await AppointmentNotifications.OnCancelledAsync(db, appointment, hasEmail, nowUtc, ct);
            db.Notifications.Add(AppointmentNotifications.NewNotification(appointment, NotificationKinds.AutoCancelled, nowUtc));
        }

        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    // One transaction per reminder: a row that was sent is committed before the next one is
    // tried, so a later failure never rolls it back (which would re-send it with a new token).
    public static async Task<int> SendDueAsync(
        AnamnysDbContext db, IEmailSender sender, Uri portalBaseUrl, DateTimeOffset now, CancellationToken ct, Guid? providerId = null, ILogger? logger = null)
    {
        var nowUtc = now.ToUniversalTime();
        db.ChangeTracker.Clear();
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT r."Id" AS "Value" FROM "Reminders" r
            JOIN "Appointments" a ON a."Id" = r."AppointmentId"
            WHERE r."DeliveryStatus" = 'pending' AND r."Channel" = 'email' AND r."ScheduledFor" <= {nowUtc}
              AND (CAST({providerId} AS uuid) IS NULL OR a."ProviderId" = CAST({providerId} AS uuid))
            ORDER BY r."ScheduledFor"
            LIMIT {BatchSize}
            """).ToListAsync(ct);

        var sent = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            try
            {
                if (await ProcessAsync(db, sender, portalBaseUrl, id, nowUtc, logger, ct))
                {
                    sent++;
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                LogFailure(logger, id, e);
                // The row's transaction was rolled back; count the failed attempt on its own.
                db.ChangeTracker.Clear();
                await RecordFailureAsync(db, id, ct);
            }
        }
        db.ChangeTracker.Clear();
        return sent;
    }

    // Only the reminder id and the exception type: never the recipient, address or message content.
    private static void LogFailure(ILogger? logger, Guid reminderId, Exception e) =>
        logger?.LogWarning("Appointment e-mail for reminder {ReminderId} failed: {ExceptionType}.", reminderId, e.GetType().Name);

    private static async Task<Reminder?> LockPendingAsync(AnamnysDbContext db, Guid id, CancellationToken ct) =>
        (await db.Reminders.FromSql($"""
            SELECT * FROM "Reminders" WHERE "Id" = {id} AND "DeliveryStatus" = 'pending'
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct)).SingleOrDefault();

    // A transient database failure after the e-mail went out replays this unit, which can send
    // the e-mail twice; that is the price of not holding a send outside any transaction.
    private static Task<bool> ProcessAsync(
        AnamnysDbContext db, IEmailSender sender, Uri portalBaseUrl, Guid id, DateTimeOffset nowUtc, ILogger? logger, CancellationToken ct) =>
        db.InTransactionAsync(async ct =>
        {
            var reminder = await LockPendingAsync(db, id, ct);
            if (reminder is null)
            {
                return false;
            }
            var sent = await SendOneAsync(db, sender, portalBaseUrl, reminder, nowUtc, logger, ct);
            await db.SaveChangesAsync(ct);
            return sent;
        }, ct);

    private static Task RecordFailureAsync(AnamnysDbContext db, Guid id, CancellationToken ct) =>
        db.InTransactionAsync(async ct =>
        {
            var reminder = await LockPendingAsync(db, id, ct);
            if (reminder is null)
            {
                return false;
            }
            reminder.Attempts++;
            if (reminder.Attempts >= ConfirmationRules.MaxAttempts)
            {
                reminder.DeliveryStatus = "failed";
            }
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

    private static async Task<bool> SendOneAsync(
        AnamnysDbContext db, IEmailSender sender, Uri portalBaseUrl, Reminder reminder, DateTimeOffset nowUtc, ILogger? logger, CancellationToken ct)
    {
        var row = await (
            from a in db.Appointments
            join p in db.Patients on new { Id = a.PatientId, a.ProviderId } equals new { p.Id, p.ProviderId }
            join pr in db.Providers on a.ProviderId equals pr.Id
            where a.Id == reminder.AppointmentId
            select new { Appointment = a, p.FirstName, p.ContactEmail, ProviderName = pr.Name })
            .SingleAsync(ct);
        var appointment = row.Appointment;

        var active = appointment.Status is AppointmentStatus.Scheduled or AppointmentStatus.Confirmed;
        var stale = row.ContactEmail is null
            || (reminder.TemplateKey != ReminderTemplates.Cancellation && !active);
        var confirmation = reminder.ConfirmationId is { } confirmationId
            ? await db.AppointmentConfirmations.SingleOrDefaultAsync(c => c.Id == confirmationId, ct)
            : null;
        var open = confirmation is { ConfirmedAt: null, ClosedAt: null } && nowUtc < confirmation.ExpiresAt;
        if (reminder.TemplateKey == ReminderTemplates.Confirmation && !open)
        {
            stale = true;
        }
        if (stale)
        {
            reminder.DeliveryStatus = "cancelled";
            return false;
        }

        AppointmentConfirmationToken? token = null;
        Uri? link = null;
        if (open)
        {
            var plain = PatientInvitationTokens.NewToken();
            token = new AppointmentConfirmationToken
            {
                Id = Guid.NewGuid(),
                ConfirmationId = confirmation!.Id,
                TokenHash = PatientInvitationTokens.Hash(plain),
                CreatedAt = nowUtc,
            };
            db.AppointmentConfirmationTokens.Add(token);
            link = new Uri($"{portalBaseUrl.ToString().TrimEnd('/')}/confirmar/{plain}");
        }

        var data = new AppointmentEmailData(
            row.ContactEmail!, row.FirstName, row.ProviderName, appointment.StartsAt, appointment.EndsAt,
            appointment.Timezone, appointment.Modality);
        DateTimeOffset? deadline = null;
        if (reminder.TemplateKey == ReminderTemplates.Confirmation)
        {
            var policy = await db.BookingPolicies
                .Where(b => b.ProviderId == appointment.ProviderId)
                .Select(b => new { b.AutoCancelMode, b.AutoCancelHours })
                .SingleOrDefaultAsync(ct);
            deadline = ConfirmationRules.DeadlineAt(
                policy?.AutoCancelMode ?? ConfirmationRules.DefaultMode,
                policy?.AutoCancelHours ?? ConfirmationRules.DefaultHours,
                nowUtc, appointment.StartsAt);
        }

        try
        {
            // Composing can fail too (AppointmentEmails throws for an unknown Timezone); that
            // counts as a failed attempt for this row, not an abort of the whole batch.
            var message = reminder.TemplateKey switch
            {
                ReminderTemplates.Confirmation => AppointmentEmails.Confirmation(data, link!, deadline),
                ReminderTemplates.Reminder1h => AppointmentEmails.Reminder(data, link),
                _ => AppointmentEmails.Cancellation(data),
            };
            await sender.SendAsync(message, ct);
        }
        catch (Exception e) when (e is EmailSendException or HttpRequestException or TimeZoneNotFoundException or InvalidTimeZoneException
            || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            LogFailure(logger, reminder.Id, e);
            if (token is not null)
            {
                db.AppointmentConfirmationTokens.Remove(token);
            }
            reminder.Attempts++;
            if (reminder.Attempts >= ConfirmationRules.MaxAttempts)
            {
                reminder.DeliveryStatus = "failed";
            }
            return false;
        }

        reminder.DeliveryStatus = "sent";
        reminder.SentAt = nowUtc;
        if (reminder.TemplateKey == ReminderTemplates.Confirmation)
        {
            confirmation!.DeadlineAt = deadline;
        }
        return true;
    }
}
