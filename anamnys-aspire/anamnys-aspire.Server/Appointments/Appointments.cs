using Anamnys.Server.Availability;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Notifications;
using Anamnys.Server.Profile;
using Anamnys.Server.Retention;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.Appointments;

public enum AppointmentOutcome { Ok, NotFound, Overlap, PatientArchived, InvalidTransition, NotStarted, Changed, OutsideAvailability }

// The outcome of a create or reschedule, with the reasons when it falls outside the provider's availability
// (design/specs/2026-11-09-availability-design.md §3, §4).
public sealed record AppointmentWriteResult(
    AppointmentOutcome Outcome, Guid? Id = null, IReadOnlyList<OutsideAvailabilityReason>? Reasons = null);

// Provider-scoped appointment operations. See
// design/specs/2026-10-05-provider-calendar-design.md §3. Every call resolves the
// appointment (or patient) with ProviderId == providerId, so a foreign id is
// indistinguishable from a missing one. Overlap is never pre-checked: the database's
// Appointments_no_overlap constraint decides, and its violation becomes Overlap.
public static class Appointments
{
    public const string PracticeTimezone = "America/Sao_Paulo";
    private const string OverlapConstraint = "Appointments_no_overlap";
    private const string CancelConstraint = "Appointments_Cancel_ck";
    private const string ClosedConstraint = "AppointmentConfirmations_Closed_ck";

    public static async Task<AppointmentListResponse> ListAsync(
        AnamnysDbContext db, Guid providerId, DateTimeOffset from, DateTimeOffset to, string[] statuses, CancellationToken cancellationToken)
    {
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        var query =
            from a in db.Appointments.AsNoTracking()
            join p in db.Patients.AsNoTracking() on a.PatientId equals p.Id
            where a.ProviderId == providerId && p.ProviderId == providerId
                && a.StartsAt < toUtc && a.EndsAt > fromUtc
            select new { a, p };
        if (statuses.Length > 0)
        {
            query = query.Where(x => statuses.Contains(x.a.Status));
        }

        var items = await query
            .OrderBy(x => x.a.StartsAt)
            .ThenBy(x => x.a.Status == "cancelled" ? 0 : 1)
            .ThenBy(x => x.a.Id)
            .Select(x => new AppointmentItem(
                x.a.Id, x.a.PatientId, x.p.FirstName + " " + x.p.LastName,
                x.a.StartsAt, x.a.EndsAt, x.a.Timezone, x.a.Modality, x.a.Status, x.a.CancellationReason,
                db.AppointmentConfirmations
                    .Where(c => c.AppointmentId == x.a.Id && c.ConfirmedAt == null && c.ClosedAt == null)
                    .Select(c => c.DeadlineAt).FirstOrDefault(),
                x.p.ContactEmail != null,
                x.a.SessionId != null && db.Notes.Any(n => n.SessionId == x.a.SessionId)))
            .ToListAsync(cancellationToken);
        return new AppointmentListResponse(items);
    }

    public static async Task<(AppointmentOutcome Outcome, Guid? Id)> CreateAsync(
        AnamnysDbContext db, Guid providerId, CreateAppointmentRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = await CreateWithReasonsAsync(db, providerId, request, now, cancellationToken);
        return (result.Outcome, result.Id);
    }

    // Create, running the outside-availability check first: with reasons and no acknowledgement nothing is written and
    // the reasons come back; acknowledged, the appointment is saved with an appointment_outside_availability entry.
    public static async Task<AppointmentWriteResult> CreateWithReasonsAsync(
        AnamnysDbContext db, Guid providerId, CreateAppointmentRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking()
            .Where(p => p.Id == request.PatientId && p.ProviderId == providerId)
            .Select(p => new { p.ArchivedAt, p.ContactEmail })
            .SingleOrDefaultAsync(cancellationToken);
        if (patient is null)
        {
            return new AppointmentWriteResult(AppointmentOutcome.NotFound);
        }
        if (patient.ArchivedAt is not null)
        {
            return new AppointmentWriteResult(AppointmentOutcome.PatientArchived);
        }

        var startsAt = request.StartsAt!.Value.ToUniversalTime();
        var endsAt = startsAt.AddMinutes(request.DurationMinutes!.Value);
        var reasons = await AvailabilityCheck.ReasonsAsync(db, providerId, startsAt, endsAt, cancellationToken);
        if (reasons.Count > 0 && !request.AcknowledgeOutsideAvailability)
        {
            return new AppointmentWriteResult(AppointmentOutcome.OutsideAvailability, Reasons: reasons);
        }

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            PatientId = request.PatientId!.Value,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Timezone = PracticeTimezone,
            Modality = request.Modality!,
            Status = AppointmentStatus.Scheduled,
            CreatedBy = "provider",
            CreatedAt = now.ToUniversalTime(),
        };
        db.Appointments.Add(appointment);
        await AppointmentNotifications.OnScheduledAsync(
            db, appointment, patient.ContactEmail is not null, now.ToUniversalTime(), cancellationToken);
        if (reasons.Count > 0)
        {
            AvailabilityCheck.AddAudit(db, providerId, appointment.Id, reasons, now);
        }

        var outcome = await SaveAsync(db, cancellationToken);
        return new AppointmentWriteResult(outcome, outcome == AppointmentOutcome.Ok ? appointment.Id : null);
    }

    public static async Task<AppointmentOutcome> UpdateAsync(
        AnamnysDbContext db, Guid providerId, Guid appointmentId, UpdateAppointmentRequest request, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await UpdateWithReasonsAsync(db, providerId, appointmentId, request, now, cancellationToken)).Outcome;

    // Reschedule, with the same outside-availability check as create when the time changes (a modality-only change
    // is not a reschedule and is never checked).
    public static async Task<AppointmentWriteResult> UpdateWithReasonsAsync(
        AnamnysDbContext db, Guid providerId, Guid appointmentId, UpdateAppointmentRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var appointment = await db.Appointments
            .SingleOrDefaultAsync(a => a.Id == appointmentId && a.ProviderId == providerId, cancellationToken);
        if (appointment is null)
        {
            return new AppointmentWriteResult(AppointmentOutcome.NotFound);
        }
        if (appointment.Status is not (AppointmentStatus.Scheduled or AppointmentStatus.Confirmed))
        {
            return new AppointmentWriteResult(AppointmentOutcome.InvalidTransition);
        }

        var startsAt = request.StartsAt!.Value.ToUniversalTime();
        var endsAt = startsAt.AddMinutes(request.DurationMinutes!.Value);
        var timeChanged = appointment.StartsAt != startsAt || appointment.EndsAt != endsAt;
        if (!timeChanged && appointment.Modality == request.Modality)
        {
            return new AppointmentWriteResult(AppointmentOutcome.Ok, appointment.Id);
        }

        IReadOnlyList<OutsideAvailabilityReason> reasons = [];
        if (timeChanged)
        {
            reasons = await AvailabilityCheck.ReasonsAsync(db, providerId, startsAt, endsAt, cancellationToken);
            if (reasons.Count > 0 && !request.AcknowledgeOutsideAvailability)
            {
                return new AppointmentWriteResult(AppointmentOutcome.OutsideAvailability, Reasons: reasons);
            }
            if (reasons.Count > 0)
            {
                AvailabilityCheck.AddAudit(db, providerId, appointment.Id, reasons, now);
            }
        }

        var hasEmail = await PatientHasEmailAsync(db, providerId, appointment.PatientId, cancellationToken);
        appointment.StartsAt = startsAt;
        appointment.EndsAt = endsAt;
        appointment.Modality = request.Modality!;
        // A new time needs a new confirmation, even if the old one was already confirmed.
        appointment.Status = AppointmentStatus.Scheduled;
        await AppointmentNotifications.OnScheduledAsync(db, appointment, hasEmail, now.ToUniversalTime(), cancellationToken);
        return new AppointmentWriteResult(await SaveAsync(db, cancellationToken), appointment.Id);
    }

    public static async Task<AppointmentOutcome> SetStatusAsync(
        AnamnysDbContext db, Guid providerId, Guid appointmentId, ChangeAppointmentStatusRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var appointment = await db.Appointments
            .SingleOrDefaultAsync(a => a.Id == appointmentId && a.ProviderId == providerId, cancellationToken);
        if (appointment is null)
        {
            return AppointmentOutcome.NotFound;
        }

        var from = appointment.Status;
        var to = request.Status!;
        if (!AppointmentTransitions.IsAllowed(from, to))
        {
            return AppointmentOutcome.InvalidTransition;
        }
        var nowUtc = now.ToUniversalTime();
        if ((to is AppointmentStatus.Attended or AppointmentStatus.NoShow) && appointment.StartsAt > nowUtc)
        {
            return AppointmentOutcome.NotStarted;
        }

        appointment.Status = to;

        switch (to)
        {
            case AppointmentStatus.Confirmed:
                await AppointmentNotifications.OnConfirmedAsync(db, appointment, "provider", nowUtc, cancellationToken);
                break;
            case AppointmentStatus.Cancelled:
                await CancelByProviderAsync(db, appointment, ProfileText.Clean(request.Reason), nowUtc, cancellationToken);
                break;
            case AppointmentStatus.Attended:
                await ApplyAttendedAsync(db, appointment, nowUtc, cancellationToken);
                break;
            case AppointmentStatus.NoShow:
                await AppointmentNotifications.OnLeftActiveAsync(db, appointment.Id, nowUtc, cancellationToken);
                break;
        }

        if (from == AppointmentStatus.Attended)
        {
            var patient = await db.Patients
                .SingleAsync(p => p.Id == appointment.PatientId && p.ProviderId == providerId, cancellationToken);
            // Undoing "attended": the latest remaining attended visit, if any.
            var latest = await db.Appointments
                .Where(a => a.PatientId == appointment.PatientId && a.ProviderId == providerId
                    && a.Id != appointment.Id && a.Status == AppointmentStatus.Attended)
                .MaxAsync(a => (DateTimeOffset?)a.StartsAt, cancellationToken);
            if (latest is not null)
            {
                patient.LastVisit = latest;
            }
        }

        // One SaveChanges: the status and LastVisit change in one transaction.
        var outcome = await SaveAsync(db, cancellationToken);
        // An appointment marked attended is a record of the patient (F-06). After the save, so a refused change
        // never moves the deadline; undoing "attended" recomputes it from the records that remain.
        if (outcome == AppointmentOutcome.Ok && to == AppointmentStatus.Attended)
        {
            await RetentionClock.PushAsync(db, providerId, appointment.PatientId, appointment.StartsAt, cancellationToken);
        }
        else if (outcome == AppointmentOutcome.Ok && from == AppointmentStatus.Attended)
        {
            await RetentionClock.RecomputeAsync(db, cancellationToken, providerId: providerId, patientId: appointment.PatientId);
        }
        return outcome;
    }

    // The provider's cancellation: status, who, when and why, and the patient's cancellation notice. Never saves, so
    // callers keep one unit of work (the status change, or a block cancelling the appointments it covers).
    internal static async Task CancelByProviderAsync(
        AnamnysDbContext db, Appointment appointment, string? reason, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        appointment.Status = AppointmentStatus.Cancelled;
        appointment.CancelledAt = nowUtc;
        appointment.CancelledBy = "provider";
        appointment.CancellationReason = reason;
        var hasEmail = await PatientHasEmailAsync(db, appointment.ProviderId, appointment.PatientId, cancellationToken);
        await AppointmentNotifications.OnCancelledAsync(db, appointment, hasEmail, nowUtc, cancellationToken);
    }

    // Side effects of an appointment becoming attended; never saves, so callers keep one unit of work.
    internal static async Task ApplyAttendedAsync(AnamnysDbContext db, Appointment appointment, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        appointment.Status = AppointmentStatus.Attended;
        await AppointmentNotifications.OnLeftActiveAsync(db, appointment.Id, nowUtc, cancellationToken);
        var patient = await db.Patients
            .SingleAsync(p => p.Id == appointment.PatientId && p.ProviderId == appointment.ProviderId, cancellationToken);
        if (patient.LastVisit is null || patient.LastVisit < appointment.StartsAt)
        {
            patient.LastVisit = appointment.StartsAt;
        }
    }

    private static Task<bool> PatientHasEmailAsync(AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken cancellationToken) =>
        db.Patients.AsNoTracking()
            .AnyAsync(p => p.Id == patientId && p.ProviderId == providerId && p.ContactEmail != null, cancellationToken);

    // The constraint is "UNIQUE ... WITHOUT OVERLAPS" (PostgreSQL 18); match it by name rather
    // than by SQLSTATE so the exact code the server reports for it does not matter.
    internal static async Task<AppointmentOutcome> SaveAsync(AnamnysDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return AppointmentOutcome.Ok;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { ConstraintName: OverlapConstraint })
        {
            db.ChangeTracker.Clear();
            return AppointmentOutcome.Overlap;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { ConstraintName: CancelConstraint or ClosedConstraint })
        {
            // A provider edit raced the worker's auto-cancel or a patient's confirmation.
            db.ChangeTracker.Clear();
            return AppointmentOutcome.Changed;
        }
    }
}
