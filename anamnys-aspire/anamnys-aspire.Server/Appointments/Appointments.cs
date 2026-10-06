using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Profile;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.Appointments;

public enum AppointmentOutcome { Ok, NotFound, Overlap, PatientArchived, InvalidTransition, NotStarted }

// Provider-scoped appointment operations. See
// design/specs/2026-10-05-provider-calendar-design.md §3. Every call resolves the
// appointment (or patient) with ProviderId == providerId, so a foreign id is
// indistinguishable from a missing one. Overlap is never pre-checked: the database's
// Appointments_no_overlap constraint decides, and its violation becomes Overlap.
public static class Appointments
{
    public const string PracticeTimezone = "America/Sao_Paulo";
    private const string OverlapConstraint = "Appointments_no_overlap";

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
            .Select(x => new AppointmentItem(
                x.a.Id, x.a.PatientId, x.p.FirstName + " " + x.p.LastName,
                x.a.StartsAt, x.a.EndsAt, x.a.Timezone, x.a.Modality, x.a.Status, x.a.CancellationReason))
            .ToListAsync(cancellationToken);
        return new AppointmentListResponse(items);
    }

    public static async Task<(AppointmentOutcome Outcome, Guid? Id)> CreateAsync(
        AnamnysDbContext db, Guid providerId, CreateAppointmentRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking()
            .Where(p => p.Id == request.PatientId && p.ProviderId == providerId)
            .Select(p => new { p.ArchivedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (patient is null)
        {
            return (AppointmentOutcome.NotFound, null);
        }
        if (patient.ArchivedAt is not null)
        {
            return (AppointmentOutcome.PatientArchived, null);
        }

        var startsAt = request.StartsAt!.Value.ToUniversalTime();
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            PatientId = request.PatientId!.Value,
            StartsAt = startsAt,
            EndsAt = startsAt.AddMinutes(request.DurationMinutes!.Value),
            Timezone = PracticeTimezone,
            Modality = request.Modality!,
            Status = AppointmentStatus.Scheduled,
            CreatedBy = "provider",
            CreatedAt = now.ToUniversalTime(),
        };
        db.Appointments.Add(appointment);

        var outcome = await SaveAsync(db, cancellationToken);
        return (outcome, outcome == AppointmentOutcome.Ok ? appointment.Id : null);
    }

    public static async Task<AppointmentOutcome> UpdateAsync(
        AnamnysDbContext db, Guid providerId, Guid appointmentId, UpdateAppointmentRequest request, CancellationToken cancellationToken)
    {
        var appointment = await db.Appointments
            .SingleOrDefaultAsync(a => a.Id == appointmentId && a.ProviderId == providerId, cancellationToken);
        if (appointment is null)
        {
            return AppointmentOutcome.NotFound;
        }
        if (appointment.Status is not (AppointmentStatus.Scheduled or AppointmentStatus.Confirmed))
        {
            return AppointmentOutcome.InvalidTransition;
        }

        var startsAt = request.StartsAt!.Value.ToUniversalTime();
        appointment.StartsAt = startsAt;
        appointment.EndsAt = startsAt.AddMinutes(request.DurationMinutes!.Value);
        appointment.Modality = request.Modality!;
        return await SaveAsync(db, cancellationToken);
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
        if (to == AppointmentStatus.Cancelled)
        {
            appointment.CancelledAt = nowUtc;
            appointment.CancelledBy = "provider";
            appointment.CancellationReason = ProfileText.Clean(request.Reason);
        }

        if (to == AppointmentStatus.Attended || from == AppointmentStatus.Attended)
        {
            var patient = await db.Patients
                .SingleAsync(p => p.Id == appointment.PatientId && p.ProviderId == providerId, cancellationToken);
            if (to == AppointmentStatus.Attended)
            {
                if (patient.LastVisit is null || patient.LastVisit < appointment.StartsAt)
                {
                    patient.LastVisit = appointment.StartsAt;
                }
            }
            else
            {
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
        }

        // One SaveChanges: the status and LastVisit change in one transaction.
        return await SaveAsync(db, cancellationToken);
    }

    // The constraint is "UNIQUE ... WITHOUT OVERLAPS" (PostgreSQL 18); match it by name rather
    // than by SQLSTATE so the exact code the server reports for it does not matter.
    private static async Task<AppointmentOutcome> SaveAsync(AnamnysDbContext db, CancellationToken cancellationToken)
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
    }
}
