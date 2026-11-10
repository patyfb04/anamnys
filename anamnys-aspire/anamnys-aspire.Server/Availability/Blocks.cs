using Anamnys.Server.Appointments;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Profile;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Availability;

public enum BlockOutcome { Ok, NotFound, InvalidRange, InvalidKind, InvalidReason, ExpectedRequired, Changed }

public sealed record BlockResult(BlockOutcome Outcome, Guid? Id = null, IReadOnlyList<Guid>? CancelledAppointmentIds = null);

// Blocks of the provider's time: block, vacation, local holiday (design/specs/2026-11-09-availability-design.md §3, §4).
// Creating a block never touches appointments unless the request says cancelAffected: true. Then it must also carry
// expectedAppointmentIds, the list the preview showed: the affected appointments are found again inside the transaction
// and, when they are exactly that list, cancelled through the provider's cancellation path (reason "ausência do
// profissional", the patient's cancellation notice); when the list changed, nothing is written (Changed). Affected means
// scheduled or confirmed, overlapping the block and not yet over. Deleting a block never touches appointments. Block
// reasons never go to logs or audit values.
public static class Blocks
{
    public const string CreatedAction = "block_created";
    public const string DeletedAction = "block_deleted";
    public const string AuditField = "block";
    public const string CancellationReason = "ausência do profissional";
    public const int MaxReason = 200;
    public const int MaxDays = 366;

    private static readonly string[] Active = [AppointmentStatus.Scheduled, AppointmentStatus.Confirmed];

    public sealed record Normalized(string Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool AllDay, string? Reason);

    // Checks a block and turns it into stored bounds. An all-day block runs from local midnight of startsAt's local date
    // to local midnight after endsAt's local date (both dates included), in the provider's zone.
    public static (BlockOutcome Outcome, Normalized? Block) Normalize(BlockRequest? request)
    {
        if (request?.Kind is not { } kind || !BlockKinds.All.Contains(kind))
        {
            return (BlockOutcome.InvalidKind, null);
        }
        if (request.StartsAt is not { } startsAt || request.EndsAt is not { } endsAt)
        {
            return (BlockOutcome.InvalidRange, null);
        }
        if (request.AllDay)
        {
            var zone = AvailabilityTime.Provider;
            var first = AvailabilityTime.LocalDate(startsAt, zone);
            var last = AvailabilityTime.LocalDate(endsAt, zone);
            if (last < first)
            {
                return (BlockOutcome.InvalidRange, null);
            }
            startsAt = AvailabilityTime.DayStart(first, zone);
            endsAt = AvailabilityTime.DayStart(last.AddDays(1), zone);
        }
        startsAt = startsAt.ToUniversalTime();
        endsAt = endsAt.ToUniversalTime();
        if (endsAt <= startsAt || endsAt - startsAt > TimeSpan.FromDays(MaxDays + 1))
        {
            return (BlockOutcome.InvalidRange, null);
        }
        var reason = ProfileText.Clean(request.Reason);
        if (reason?.Length > MaxReason)
        {
            return (BlockOutcome.InvalidReason, null);
        }
        return (BlockOutcome.Ok, new Normalized(kind, startsAt, endsAt, request.AllDay, reason));
    }

    // The provider's blocks overlapping [from, to), newest first.
    public static async Task<IReadOnlyList<BlockItem>> OverlappingAsync(
        AnamnysDbContext db, Guid providerId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        return await db.AvailabilityExceptions.AsNoTracking()
            .Where(b => b.ProviderId == providerId && b.StartsAt < toUtc && b.EndsAt > fromUtc)
            .OrderByDescending(b => b.StartsAt)
            .ThenBy(b => b.Id)
            .Select(b => new BlockItem(b.Id, b.Kind, b.StartsAt, b.EndsAt, b.AllDay, b.Reason))
            .ToListAsync(ct);
    }

    // The active (scheduled or confirmed) appointments a block would overlap that are not over yet.
    public static async Task<(BlockOutcome Outcome, IReadOnlyList<AffectedAppointment>? Items)> PreviewAsync(
        AnamnysDbContext db, Guid providerId, BlockRequest? request, DateTimeOffset now, CancellationToken ct)
    {
        var (outcome, block) = Normalize(request);
        if (block is null)
        {
            return (outcome, null);
        }
        var nowUtc = now.ToUniversalTime();
        var items = await (
            from a in Affected(db, providerId, block, nowUtc).AsNoTracking()
            join p in db.Patients.AsNoTracking() on a.PatientId equals p.Id
            where p.ProviderId == providerId
            orderby a.StartsAt, a.Id
            select new AffectedAppointment(a.Id, a.StartsAt, a.EndsAt, a.PatientId, p.FirstName + " " + p.LastName))
            .ToListAsync(ct);
        return (BlockOutcome.Ok, items);
    }

    private static IQueryable<Appointment> Affected(AnamnysDbContext db, Guid providerId, Normalized block, DateTimeOffset nowUtc) =>
        db.Appointments.Where(a => a.ProviderId == providerId && Active.Contains(a.Status)
            && a.StartsAt < block.EndsAt && a.EndsAt > block.StartsAt && a.EndsAt > nowUtc);

    public static async Task<BlockResult> CreateAsync(
        AnamnysDbContext db, Guid providerId, BlockRequest? request, DateTimeOffset now, CancellationToken ct)
    {
        var (outcome, block) = Normalize(request);
        if (block is null)
        {
            return new BlockResult(outcome);
        }
        if (request!.CancelAffected && request.ExpectedAppointmentIds is null)
        {
            return new BlockResult(BlockOutcome.ExpectedRequired);
        }
        var nowUtc = now.ToUniversalTime();
        return await db.InTransactionAsync(async c =>
        {
            await AvailabilityRules.LockProviderAsync(db, providerId, c);
            List<Appointment> affected = [];
            if (request.CancelAffected)
            {
                affected = await Affected(db, providerId, block, nowUtc).OrderBy(a => a.StartsAt).ThenBy(a => a.Id).ToListAsync(c);
                if (!affected.Select(a => a.Id).ToHashSet().SetEquals(request.ExpectedAppointmentIds!))
                {
                    return new BlockResult(BlockOutcome.Changed);
                }
            }
            var row = new AvailabilityException
            {
                Id = Guid.NewGuid(),
                ProviderId = providerId,
                StartsAt = block.StartsAt,
                EndsAt = block.EndsAt,
                AllDay = block.AllDay,
                Kind = block.Kind,
                Reason = block.Reason,
            };
            db.AvailabilityExceptions.Add(row);

            var cancelled = new List<Guid>();
            foreach (var appointment in affected)
            {
                await Appointments.Appointments.CancelByProviderAsync(db, appointment, CancellationReason, nowUtc, c);
                cancelled.Add(appointment.Id);
            }

            AvailabilityRules.AddAudit(db, providerId, CreatedAction, AuditField,
                $"{row.Id} {row.Kind} {row.StartsAt:O} {row.EndsAt:O} cancelled {cancelled.Count}", nowUtc);
            var saved = await Appointments.Appointments.SaveAsync(db, c);
            return saved == AppointmentOutcome.Ok
                ? new BlockResult(BlockOutcome.Ok, row.Id, cancelled)
                : new BlockResult(BlockOutcome.Changed);
        }, ct);
    }

    public static async Task<BlockOutcome> DeleteAsync(AnamnysDbContext db, Guid providerId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.AvailabilityExceptions.SingleOrDefaultAsync(b => b.Id == id && b.ProviderId == providerId, ct);
        if (row is null)
        {
            return BlockOutcome.NotFound;
        }
        db.AvailabilityExceptions.Remove(row);
        AvailabilityRules.AddAudit(db, providerId, DeletedAction, AuditField, $"{row.Id} {row.Kind}", now);
        await db.SaveChangesAsync(ct);
        return BlockOutcome.Ok;
    }
}
