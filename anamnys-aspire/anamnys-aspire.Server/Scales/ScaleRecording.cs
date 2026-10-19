using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Scales;

public sealed record RecordScaleRequest(Guid? InstrumentId, int? Score, DateTimeOffset? AppliedAt, Guid? SessionId);

public enum RecordOutcome { Recorded, Invalid, NotFound, Archived, Unavailable, OptInRequired }

public sealed record RecordResult(RecordOutcome Outcome, Guid? Id, Dictionary<string, string[]>? Errors, string? Explanation);

public sealed record ScaleEntry(Guid Id, int Score, DateTimeOffset AppliedAt, Guid? SessionId, DateTimeOffset? AppointmentStartsAt);

public sealed record ScaleSeries(Guid InstrumentId, string Code, string Name, int MinScore, int MaxScore, IReadOnlyList<ScaleEntry> Entries);

// Only the score and the date are stored (design/specs/2026-10-18-psychometric-scales-design.md).
public static class ScaleRecording
{
    // A client clock a little ahead of the server is accepted; the stored date is capped at server time.
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    public static async Task<RecordResult> RecordAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, RecordScaleRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.InstrumentId is null)
        {
            errors["instrumentId"] = ["Escolha o instrumento."];
        }
        if (request.Score is null)
        {
            errors["score"] = ["Informe o escore."];
        }
        if (request.AppliedAt is null)
        {
            errors["appliedAt"] = ["Informe a data."];
        }
        else if (request.AppliedAt > now + ClockTolerance)
        {
            errors["appliedAt"] = ["A data não pode estar no futuro."];
        }
        if (errors.Count > 0)
        {
            return new RecordResult(RecordOutcome.Invalid, null, errors, null);
        }

        var patient = await db.Patients.AsNoTracking()
            .Where(p => p.Id == patientId && p.ProviderId == providerId)
            .Select(p => new { p.ArchivedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (patient is null)
        {
            return new RecordResult(RecordOutcome.NotFound, null, null, null);
        }
        if (patient.ArchivedAt is not null)
        {
            return new RecordResult(RecordOutcome.Archived, null, null, null);
        }

        var instrumentId = request.InstrumentId!.Value;
        var instrument = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(i => i.Id == instrumentId, cancellationToken);
        if (instrument is null)
        {
            return new RecordResult(RecordOutcome.NotFound, null, null, null);
        }

        var optedIn = await db.ProviderInstrumentOptIns.AnyAsync(
            o => o.ProviderId == providerId && o.InstrumentId == instrumentId && o.RevokedAt == null, cancellationToken);
        switch (ScaleCatalog.RegimeFor(instrument, optedIn))
        {
            case ScaleRegime.Unavailable:
                return new RecordResult(RecordOutcome.Unavailable, null, null, instrument.StatusExplanation);
            case ScaleRegime.NeedsOptIn:
                return new RecordResult(RecordOutcome.OptInRequired, null, null, instrument.StatusExplanation);
        }

        var score = request.Score!.Value;
        if (score < instrument.MinScore || score > instrument.MaxScore)
        {
            return new RecordResult(
                RecordOutcome.Invalid,
                null,
                new Dictionary<string, string[]> { ["score"] = [$"O escore do {instrument.Code} vai de {instrument.MinScore} a {instrument.MaxScore}."] },
                null);
        }

        if (request.SessionId is { } sessionId
            && !await db.Sessions.AnyAsync(s => s.Id == sessionId && s.PatientId == patientId && s.ProviderId == providerId, cancellationToken))
        {
            return new RecordResult(RecordOutcome.NotFound, null, null, null);
        }

        var application = new ScaleApplication
        {
            Id = Guid.NewGuid(),
            PatientId = patientId,
            SessionId = request.SessionId,
            InstrumentId = instrumentId,
            Score = score,
            AppliedAt = request.AppliedAt!.Value < now ? request.AppliedAt!.Value : now,
            AppliedBy = providerId,
            CreatedAt = now,
        };
        db.ScaleApplications.Add(application);
        await db.SaveChangesAsync(cancellationToken);
        return new RecordResult(RecordOutcome.Recorded, application.Id, null, null);
    }

    public static async Task<bool> RemoveAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid applicationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var application = await db.ScaleApplications.SingleOrDefaultAsync(
            a => a.Id == applicationId
                && a.PatientId == patientId
                && a.RemovedAt == null
                && db.Patients.Any(p => p.Id == a.PatientId && p.ProviderId == providerId),
            cancellationToken);
        if (application is null)
        {
            return false;
        }

        application.RemovedAt = now;
        application.RemovedBy = providerId;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task<IReadOnlyList<ScaleSeries>?> SeriesAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, Guid? sessionId, CancellationToken cancellationToken)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.ProviderId == providerId, cancellationToken))
        {
            return null;
        }

        var rows = await (
            from a in db.ScaleApplications.AsNoTracking()
            join i in db.Instruments on a.InstrumentId equals i.Id
            where a.PatientId == patientId && a.RemovedAt == null && (sessionId == null || a.SessionId == sessionId)
            select new
            {
                a.Id,
                a.Score,
                a.AppliedAt,
                a.SessionId,
                AppointmentStartsAt = a.SessionId == null
                    ? (DateTimeOffset?)null
                    : db.Appointments.Where(x => x.SessionId == a.SessionId && x.ProviderId == providerId).OrderBy(x => x.StartsAt).Select(x => (DateTimeOffset?)x.StartsAt).FirstOrDefault(),
                InstrumentId = i.Id,
                i.Code,
                i.Name,
                i.MinScore,
                i.MaxScore,
            }).ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => new { r.InstrumentId, r.Code, r.Name, r.MinScore, r.MaxScore })
            .OrderBy(g => g.Key.Code, StringComparer.Ordinal)
            .Select(g => new ScaleSeries(
                g.Key.InstrumentId,
                g.Key.Code,
                g.Key.Name,
                g.Key.MinScore,
                g.Key.MaxScore,
                g.OrderBy(r => r.AppliedAt).ThenBy(r => r.Id)
                    .Select(r => new ScaleEntry(r.Id, r.Score, r.AppliedAt, r.SessionId, r.AppointmentStartsAt))
                    .ToList()))
            .ToList();
    }
}
