using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Notifications;

// Status is "pending" | "confirmed" | "invalid"; the details are null when invalid.
public sealed record ConfirmationView(
    string Status, string? ProviderName, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Timezone, string? Modality);

public enum TokenConfirmOutcome { Confirmed, Invalid }

public enum PortalConfirmOutcome { Ok, NotFound, InvalidState }

// Confirmation by the e-mailed link (anonymous: the token is the credential) and from the
// patient portal. Opening a link only reads; confirming is a separate call. See
// design/specs/2026-10-07-appointment-notifications-design.md §4.
//
// Confirming runs in a transaction that locks the confirmation and the appointment FOR UPDATE
// (the order CancelOverdueAsync locks them in) and re-evaluates the state under the locks, so
// it serializes with the worker's auto-cancel and with a second confirm.
public static class AppointmentConfirmations
{
    private const int TokenLength = 43;

    private static readonly ConfirmationView Invalid = new("invalid", null, null, null, null, null);

    public static async Task<ConfirmationView> ViewAsync(
        AnamnysDbContext db, string token, DateTimeOffset now, CancellationToken ct)
    {
        var found = await FindAsync(db, token, now.ToUniversalTime(), ct);
        if (found is null)
        {
            return Invalid;
        }
        var (state, _, appointment, providerName) = found.Value;
        return new ConfirmationView(
            state, providerName, appointment.StartsAt, appointment.EndsAt, appointment.Timezone, appointment.Modality);
    }

    public static async Task<TokenConfirmOutcome> ConfirmByTokenAsync(
        AnamnysDbContext db, string token, DateTimeOffset now, CancellationToken ct)
    {
        var nowUtc = now.ToUniversalTime();
        var found = await FindAsync(db, token, nowUtc, ct);
        if (found is null)
        {
            return TokenConfirmOutcome.Invalid;
        }
        if (found.Value.State == "confirmed")
        {
            return TokenConfirmOutcome.Confirmed;
        }

        // The state is read again under the locks: the worker may have cancelled the
        // appointment, or a second confirm may have won, since the unlocked read above.
        var confirmationId = found.Value.ConfirmationId;
        return await db.InTransactionAsync(async ct =>
        {
            var confirmation = (await db.AppointmentConfirmations.FromSql($"""
                SELECT * FROM "AppointmentConfirmations" WHERE "Id" = {confirmationId} FOR UPDATE
                """).ToListAsync(ct)).SingleOrDefault();
            if (confirmation is null)
            {
                return TokenConfirmOutcome.Invalid;
            }
            var appointment = await LockAppointmentAsync(db, confirmation.AppointmentId, ct);
            var state = appointment is null ? null : StateOf(confirmation, appointment, nowUtc);
            if (state == "pending")
            {
                await ConfirmAsync(db, appointment!, nowUtc, ct);
            }
            return state is null ? TokenConfirmOutcome.Invalid : TokenConfirmOutcome.Confirmed;
        }, ct);
    }

    public static async Task<PortalConfirmOutcome> ConfirmFromPortalAsync(
        AnamnysDbContext db, Guid accountId, Guid appointmentId, DateTimeOffset now, CancellationToken ct)
    {
        var owned = await db.Appointments.AsNoTracking()
            .Where(a => a.Id == appointmentId)
            .Join(db.Patients.Where(p => p.AccountId == accountId), a => a.PatientId, p => p.Id, (a, _) => a.Id)
            .AnyAsync(ct);
        if (!owned)
        {
            return PortalConfirmOutcome.NotFound;
        }

        // Confirmations first, then the appointment, then the checks again under the locks.
        var nowUtc = now.ToUniversalTime();
        return await db.InTransactionAsync(async ct =>
        {
            await db.AppointmentConfirmations.FromSql($"""
                SELECT * FROM "AppointmentConfirmations"
                WHERE "AppointmentId" = {appointmentId} AND "ConfirmedAt" IS NULL AND "ClosedAt" IS NULL
                FOR UPDATE
                """).ToListAsync(ct);
            var appointment = await LockAppointmentAsync(db, appointmentId, ct);
            if (appointment is null)
            {
                return PortalConfirmOutcome.NotFound;
            }
            if (appointment.Status != AppointmentStatus.Scheduled || appointment.StartsAt <= nowUtc)
            {
                return PortalConfirmOutcome.InvalidState;
            }
            await ConfirmAsync(db, appointment, nowUtc, ct);
            return PortalConfirmOutcome.Ok;
        }, ct);
    }

    private static async Task<Appointment?> LockAppointmentAsync(AnamnysDbContext db, Guid id, CancellationToken ct) =>
        (await db.Appointments.FromSql($"""
            SELECT * FROM "Appointments" WHERE "Id" = {id} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault();

    private static async Task ConfirmAsync(AnamnysDbContext db, Appointment appointment, DateTimeOffset nowUtc, CancellationToken ct)
    {
        appointment.Status = AppointmentStatus.Confirmed;
        await AppointmentNotifications.OnConfirmedAsync(db, appointment, "patient", nowUtc, ct);
        await db.SaveChangesAsync(ct);
    }

    // Null means invalid: unknown or malformed token, closed or expired confirmation,
    // cancelled or rescheduled appointment. Unlocked read: confirming re-checks under locks.
    private static async Task<(string State, Guid ConfirmationId, Appointment Appointment, string ProviderName)?> FindAsync(
        AnamnysDbContext db, string token, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (!IsWellFormed(token))
        {
            return null;
        }

        var hash = PatientInvitationTokens.Hash(token);
        var row = await db.AppointmentConfirmationTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Join(db.AppointmentConfirmations, t => t.ConfirmationId, c => c.Id, (_, c) => c)
            .Join(db.Appointments, c => c.AppointmentId, a => a.Id, (c, a) => new { Confirmation = c, Appointment = a })
            .Join(db.Providers, x => x.Appointment.ProviderId, p => p.Id, (x, p) => new { x.Confirmation, x.Appointment, p.Name })
            .SingleOrDefaultAsync(ct);
        if (row is null || StateOf(row.Confirmation, row.Appointment, nowUtc) is not { } state)
        {
            return null;
        }
        return (state, row.Confirmation.Id, row.Appointment, row.Name);
    }

    // A confirmed confirmation's token keeps working only while the appointment is still
    // confirmed for the same time (a reschedule sets it back to scheduled at a new StartsAt).
    private static string? StateOf(AppointmentConfirmation c, Appointment a, DateTimeOffset nowUtc)
    {
        if (nowUtc >= c.ExpiresAt)
        {
            return null;
        }
        if (c.ConfirmedAt is not null)
        {
            return a.Status == AppointmentStatus.Confirmed && a.StartsAt == c.ExpiresAt ? "confirmed" : null;
        }
        return c.ClosedAt is null && a.Status == AppointmentStatus.Scheduled ? "pending" : null;
    }

    private static bool IsWellFormed(string token) =>
        token.Length == TokenLength && token.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');
}
