using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Retention;

public sealed record RetentionSettingsDto(int GuardYears, int UnsignedNoteDays);

public sealed record RetentionSettingsRequest(int? GuardYears, int? UnsignedNoteDays)
{
    public const int MaxGuardYears = 100;
    public const int MaxUnsignedNoteDays = 365;

    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (GuardYears is null or < RetentionRule.DefaultGuardYears or > MaxGuardYears)
        {
            errors["guardYears"] = [$"Use entre {RetentionRule.DefaultGuardYears} e {MaxGuardYears} anos (mínimo da Res. CFP 001/2009)."];
        }
        if (UnsignedNoteDays is null or < 1 or > MaxUnsignedNoteDays)
        {
            errors["unsignedNoteDays"] = [$"Use entre 1 e {MaxUnsignedNoteDays} dias."];
        }
        return errors;
    }
}

// The provider's "Guarda" settings (design/specs/2026-10-28-retention-panel-design.md §3): guard years live in the
// 'prontuario' RetentionRules row, the unsigned-note age on the provider. Changing the years recomputes the
// provider's deadlines in the same transaction.
public static class RetentionSettings
{
    public static async Task<RetentionSettingsDto> GetAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct)
    {
        var years = await GuardYearsAsync(db, providerId, ct);
        var days = await db.Providers.AsNoTracking().Where(p => p.Id == providerId).Select(p => p.UnsignedNoteDays).SingleAsync(ct);
        return new RetentionSettingsDto(years, days);
    }

    public static async Task<int> GuardYearsAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct) =>
        await db.RetentionRules.AsNoTracking()
            .Where(r => r.ProviderId == providerId && r.RecordKind == RetentionRule.Prontuario)
            .Select(r => (int?)r.GuardYears)
            .SingleOrDefaultAsync(ct)
        ?? RetentionRule.DefaultGuardYears;

    // The caller validates the request first.
    public static Task SaveAsync(AnamnysDbContext db, Guid providerId, RetentionSettingsRequest request, CancellationToken ct)
    {
        var years = request.GuardYears!.Value;
        var days = request.UnsignedNoteDays!.Value;
        return db.InTransactionAsync(async ct =>
        {
            await db.Providers.Where(p => p.Id == providerId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UnsignedNoteDays, days), ct);
            var previous = await GuardYearsAsync(db, providerId, ct);
            if (previous == years)
            {
                return 0;
            }
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "RetentionRules" ("ProviderId","RecordKind","GuardYears","Basis")
                VALUES ({providerId}, {RetentionRule.Prontuario}, {years}, {RetentionRule.DefaultBasis})
                ON CONFLICT ("ProviderId","RecordKind") DO UPDATE SET "GuardYears" = EXCLUDED."GuardYears"
                """, ct);
            return await RetentionClock.RecomputeAsync(db, ct, providerId: providerId);
        }, ct);
    }
}
