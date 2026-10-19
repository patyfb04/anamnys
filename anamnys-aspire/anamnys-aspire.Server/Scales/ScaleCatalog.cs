using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.Scales;

public static class ScaleRegime
{
    public const string Available = "available";
    public const string NeedsOptIn = "needs_opt_in";
    public const string OptedIn = "opted_in";
    public const string Unavailable = "unavailable";
}

public sealed record InstrumentItem(Guid Id, string Code, string Name, int MinScore, int MaxScore, string Regime, string Explanation);

public enum OptInOutcome { OptedIn, AlreadyOptedIn, NotFound, NotAllowed }

public sealed record OptInResult(OptInOutcome Outcome, string? Explanation);

// The three regimes of design/specs/2026-10-18-psychometric-scales-design.md §3.
public static class ScaleCatalog
{
    public static string RegimeFor(Instrument instrument, bool optedIn)
    {
        if (instrument.Enabled)
        {
            return ScaleRegime.Available;
        }
        if (instrument.SatepsiStatus == "sem_classificacao" && instrument.LicenseMode != "vedado_digital")
        {
            return optedIn ? ScaleRegime.OptedIn : ScaleRegime.NeedsOptIn;
        }
        return ScaleRegime.Unavailable;
    }

    public static async Task<IReadOnlyList<InstrumentItem>> ListAsync(AnamnysDbContext db, Guid providerId, CancellationToken cancellationToken)
    {
        var optedIn = (await db.ProviderInstrumentOptIns.AsNoTracking()
            .Where(o => o.ProviderId == providerId && o.RevokedAt == null)
            .Select(o => o.InstrumentId)
            .ToListAsync(cancellationToken)).ToHashSet();
        var instruments = await db.Instruments.AsNoTracking().ToListAsync(cancellationToken);

        return instruments
            .Select(i => new InstrumentItem(i.Id, i.Code, i.Name, i.MinScore, i.MaxScore, RegimeFor(i, optedIn.Contains(i.Id)), i.StatusExplanation))
            .OrderBy(i => Rank(i.Regime))
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .ToList();
    }

    public static async Task<OptInResult> OptInAsync(
        AnamnysDbContext db, Guid providerId, Guid instrumentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var instrument = await db.Instruments.AsNoTracking().SingleOrDefaultAsync(i => i.Id == instrumentId, cancellationToken);
        if (instrument is null)
        {
            return new OptInResult(OptInOutcome.NotFound, null);
        }
        if (RegimeFor(instrument, false) is not (ScaleRegime.NeedsOptIn or ScaleRegime.OptedIn))
        {
            return new OptInResult(OptInOutcome.NotAllowed, instrument.StatusExplanation);
        }
        var exists = await db.ProviderInstrumentOptIns.AnyAsync(
            o => o.ProviderId == providerId && o.InstrumentId == instrumentId && o.RevokedAt == null, cancellationToken);
        if (exists)
        {
            return new OptInResult(OptInOutcome.AlreadyOptedIn, null);
        }

        var optIn = new ProviderInstrumentOptIn
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId,
            InstrumentId = instrumentId,
            AcknowledgedAt = now,
            ExplanationShown = instrument.StatusExplanation,
        };
        db.ProviderInstrumentOptIns.Add(optIn);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(optIn).State = EntityState.Detached;
            return new OptInResult(OptInOutcome.AlreadyOptedIn, null);
        }
        return new OptInResult(OptInOutcome.OptedIn, null);
    }

    private static int Rank(string regime) => regime switch
    {
        ScaleRegime.Available => 0,
        ScaleRegime.OptedIn => 1,
        ScaleRegime.NeedsOptIn => 2,
        _ => 3,
    };
}
