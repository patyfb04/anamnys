using Anamnys.Server.Availability;
using Anamnys.Server.Data;
using Anamnys.Tests.Retention;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Availability;

// A scratch database from the bootstrap script (which carries design/migrations/2026-11-09-availability.sql), so these
// tests do not depend on the dev database having the migration applied. Now is Monday 2026-11-09, 12:00 in São Paulo.
internal sealed class AvailabilityScene : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Today = new(2026, 11, 9);

    private AvailabilityScene(RetentionScratch db, Guid provider, Guid patient, Guid otherProvider, Guid stranger)
    {
        Db = db;
        Provider = provider;
        Patient = patient;
        OtherProvider = otherProvider;
        Stranger = stranger;
    }

    public RetentionScratch Db { get; }
    public Guid Provider { get; }
    public Guid Patient { get; }
    public Guid OtherProvider { get; }
    public Guid Stranger { get; }

    public static async Task<AvailabilityScene> CreateAsync(SharedAppHostFixture fixture, CancellationToken ct)
    {
        var db = await RetentionScratch.CreateAsync(fixture, ct);
        var provider = await db.AddProviderAsync(ct);
        var patient = await db.AddPatientAsync(provider, "Benedita", ct);
        await db.ExecuteAsync("""UPDATE "Patients" SET "ContactEmail" = 'benedita@test.local' WHERE "Id" = @p""", ct, ("p", patient));
        var other = await db.AddProviderAsync(ct);
        var stranger = await db.AddPatientAsync(other, "Estranha", ct);
        return new AvailabilityScene(db, provider, patient, other, stranger);
    }

    public AnamnysDbContext CreateDbContext() => Db.CreateDbContext();

    // Weekday hours (Monday to Friday, 08:00-12:00 and 14:00-18:00) from a date, through the service.
    public async Task SaveWeekdaysAsync(DateOnly from, CancellationToken ct, string start = "08:00", string end = "12:00")
    {
        await using var db = CreateDbContext();
        var days = Enumerable.Range(1, 5)
            .Select(d => (SaveAvailabilityDay?)new SaveAvailabilityDay(d, [new(start, end), new("14:00", "18:00")]))
            .ToList();
        var outcome = await AvailabilityRules.SaveAsync(db, Provider, new SaveAvailabilityRequest(from, days), Now, ct);
        if (outcome != AvailabilityOutcome.Ok)
        {
            throw new InvalidOperationException(outcome.ToString());
        }
    }

    // A rule row written directly (past versions, other zones), bypassing the service.
    // Its version header is created on the first rule of that start date.
    public async Task AddRuleAsync(int weekday, string start, string end, DateOnly from, DateOnly? to, CancellationToken ct, string zone = "America/Sao_Paulo")
    {
        await Db.ExecuteAsync(
            """
            INSERT INTO "AvailabilityVersions" ("ProviderId","EffectiveFrom","EffectiveTo") VALUES (@p, @f::date, @t::date)
            ON CONFLICT ("ProviderId","EffectiveFrom") DO NOTHING
            """,
            ct, ("p", Provider), ("f", from), ("t", (object?)to ?? DBNull.Value));
        await Db.ExecuteAsync(
            """
            INSERT INTO "AvailabilityRules" ("ProviderId","VersionId","Weekday","StartsAtTime","EndsAtTime","EffectiveFrom","EffectiveTo","Timezone")
            SELECT @p, v."Id", @w, @s::time, @e::time, @f::date, @t::date, @z FROM "AvailabilityVersions" v WHERE v."ProviderId" = @p AND v."EffectiveFrom" = @f
            """,
            ct, ("p", Provider), ("w", weekday), ("s", start), ("e", end), ("f", from), ("t", (object?)to ?? DBNull.Value), ("z", zone));
    }

    public async Task<List<(DateOnly From, DateOnly? To)>> VersionsAsync(CancellationToken ct)
    {
        await using var db = CreateDbContext();
        var rows = await db.AvailabilityVersions.AsNoTracking().Where(v => v.ProviderId == Provider).OrderBy(v => v.EffectiveFrom).ToListAsync(ct);
        return [.. rows.Select(v => (v.EffectiveFrom, v.EffectiveTo))];
    }

    public async Task<Guid> AddBlockAsync(Guid provider, DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken ct, string kind = "block")
    {
        var id = Guid.NewGuid();
        await Db.ExecuteAsync(
            """INSERT INTO "AvailabilityExceptions" ("Id","ProviderId","StartsAt","EndsAt","Kind","Reason") VALUES (@id, @p, @s, @e, @k, 'Motivo secreto')""",
            ct, ("id", id), ("p", provider), ("s", startsAt.ToUniversalTime()), ("e", endsAt.ToUniversalTime()), ("k", kind));
        return id;
    }

    public async Task<List<(DateOnly From, DateOnly? To, int Weekday, TimeOnly Start)>> RulesAsync(CancellationToken ct)
    {
        await using var db = CreateDbContext();
        var rows = await db.AvailabilityRules.AsNoTracking().Where(r => r.ProviderId == Provider)
            .OrderBy(r => r.EffectiveFrom).ThenBy(r => r.Weekday).ThenBy(r => r.StartsAtTime)
            .ToListAsync(ct);
        return [.. rows.Select(r => (r.EffectiveFrom, r.EffectiveTo, r.Weekday, r.StartsAtTime))];
    }

    public async Task<List<string>> AuditAsync(CancellationToken ct)
    {
        await using var db = CreateDbContext();
        return await db.AuditEntries.AsNoTracking().Where(a => a.ActorId == Provider)
            .OrderBy(a => a.At).ThenBy(a => a.Id)
            .Select(a => a.Action + " " + a.FieldChanged + " " + a.NewValue)
            .ToListAsync(ct);
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
