using System.Runtime.CompilerServices;
using Anamnys.Server.Data;
using Anamnys.Server.Patients;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Retention;

// One deadline per patient record (design/specs/2026-10-28-retention-panel-design.md §1, §3): the practice-local
// date of the last record (signed note, signed clinical document, attended session, appointment marked attended)
// plus the provider's guard years for 'prontuario' (5 when the provider has no rule). Discarded patients are
// never touched.
//
// Push and recompute take the same per-provider transaction-scoped advisory lock first. A recompute therefore
// starts its snapshot only after every in-flight push of that provider has committed (or rolled back), and can
// never write a deadline older than one a concurrent signature just pushed. Both join the caller's transaction
// when there is one (the lock is then held until it commits) and open their own otherwise.
public static class RetentionClock
{
    // A new record dated `at`: moves the deadline forward, never back. Inside a caller's transaction it commits or
    // rolls back with the record.
    public static Task<int> PushAsync(AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset at, CancellationToken ct)
    {
        var on = PatientSearchQuery.PracticeToday(at);
        return LockedAsync(db, providerId, ct => db.Database.ExecuteSqlAsync($"""
            UPDATE "Patients" p
               SET "LastRecordAt" = GREATEST(p."LastRecordAt", {on}),
                   "RetentionUntil" = (GREATEST(p."LastRecordAt", {on}) + make_interval(years => COALESCE(
                       (SELECT r."GuardYears" FROM "RetentionRules" r WHERE r."ProviderId" = p."ProviderId" AND r."RecordKind" = 'prontuario'), 5)))::date
             WHERE p."Id" = {patientId} AND p."ProviderId" = {providerId} AND p."DiscardedAt" IS NULL
            """, ct), ct);
    }

    // Recomputes from the records themselves (the daily job, a guard-years change, an undone attendance).
    // providerId and patientId narrow the run; with no provider every provider is recomputed, one locked
    // transaction each. Returns the number of patients whose clock changed.
    public static async Task<int> RecomputeAsync(AnamnysDbContext db, CancellationToken ct, Guid? providerId = null, Guid? patientId = null)
    {
        if (providerId is { } one)
        {
            return await LockedAsync(db, one, ct => RecomputeProviderAsync(db, one, patientId, ct), ct);
        }
        var providers = await db.Patients.AsNoTracking()
            .Where(p => p.DiscardedAt == null && (patientId == null || p.Id == patientId))
            .Select(p => p.ProviderId).Distinct().ToListAsync(ct);
        var changed = 0;
        foreach (var provider in providers)
        {
            changed += await LockedAsync(db, provider, ct => RecomputeProviderAsync(db, provider, patientId, ct), ct);
        }
        return changed;
    }

    // The practice-local date of every record of the patient row aliased p: the one definition of "a record", shared
    // by the clock (latest date) and the disposal term (first and last date). Contains no braces, so it can be
    // spliced into a composite format string.
    internal const string RecordDatesSql = """
        SELECT (n."SignedAt" AT TIME ZONE 'America/Sao_Paulo')::date AS d FROM "Notes" n
         WHERE n."PatientId" = p."Id" AND n."ProviderId" = p."ProviderId" AND n."SignedAt" IS NOT NULL
        UNION ALL
        SELECT (c."SignedAt" AT TIME ZONE 'America/Sao_Paulo')::date FROM "ClinicalDocuments" c
         WHERE c."PatientId" = p."Id" AND c."ProviderId" = p."ProviderId" AND c."SignedAt" IS NOT NULL
        UNION ALL
        SELECT (COALESCE(s."ActualStart", s."CreatedAt") AT TIME ZONE 'America/Sao_Paulo')::date FROM "Sessions" s
         WHERE s."PatientId" = p."Id" AND s."ProviderId" = p."ProviderId" AND s."Attendance" = 'attended'
        UNION ALL
        SELECT (a."StartsAt" AT TIME ZONE 'America/Sao_Paulo')::date FROM "Appointments" a
         WHERE a."PatientId" = p."Id" AND a."ProviderId" = p."ProviderId" AND a."Status" = 'attended'
        """;

    public sealed class RecordPeriod
    {
        public DateOnly? From { get; set; }
        public DateOnly? To { get; set; }
    }

    // First and last record of one patient, by the same definition as the clock.
    public static async Task<RecordPeriod> PeriodAsync(AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken ct) =>
        (await db.Database.SqlQuery<RecordPeriod>(FormattableStringFactory.Create(
            $$"""
            SELECT min(r.d) AS "From", max(r.d) AS "To"
              FROM "Patients" p, LATERAL ({{RecordDatesSql}}) r
             WHERE p."Id" = {0} AND p."ProviderId" = {1}
            """, patientId, providerId)).ToListAsync(ct)).SingleOrDefault() ?? new RecordPeriod();

    private static Task<int> RecomputeProviderAsync(AnamnysDbContext db, Guid providerId, Guid? patientId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(FormattableStringFactory.Create($$"""
            WITH last AS (
                SELECT p."Id",
                       (SELECT max(r.d) FROM ({{RecordDatesSql}}) r) AS "At",
                       COALESCE(rr."GuardYears", 5) AS "Years"
                  FROM "Patients" p
                  LEFT JOIN "RetentionRules" rr ON rr."ProviderId" = p."ProviderId" AND rr."RecordKind" = 'prontuario'
                 WHERE p."DiscardedAt" IS NULL AND p."ProviderId" = {0}
                   AND (CAST({1} AS uuid) IS NULL OR p."Id" = CAST({1} AS uuid))
            )
            UPDATE "Patients" p
               SET "LastRecordAt" = last."At",
                   "RetentionUntil" = (last."At" + make_interval(years => last."Years"))::date
              FROM last
             WHERE p."Id" = last."Id"
               AND (p."LastRecordAt" IS DISTINCT FROM last."At"
                    OR p."RetentionUntil" IS DISTINCT FROM (last."At" + make_interval(years => last."Years"))::date)
            """, providerId, patientId), ct);

    private static async Task<int> LockedAsync(AnamnysDbContext db, Guid providerId, Func<CancellationToken, Task<int>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await LockAsync(db, providerId, ct);
            return await work(ct);
        }
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(db, providerId, ct);
            var result = await work(ct);
            await tx.CommitAsync(ct);
            return result;
        }, ct);
    }

    // The per-provider advisory lock, held until the caller's transaction ends. Anything that also locks patient rows
    // of the provider (the discard) takes this first, so every path locks in the same order.
    public static Task LockAsync(AnamnysDbContext db, Guid providerId, CancellationToken ct)
    {
        var key = $"retention:{providerId}";
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }
}
