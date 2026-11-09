using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Anamnys.Server.Data;
using Anamnys.Server.Integrity;
using Anamnys.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.RestoreCheck;

public sealed record CheckResult(string Name, bool Passed, string Detail);

public sealed record RestoreCheckReport(bool Passed, IReadOnlyList<CheckResult> Checks, double DurationSeconds);

// What the backup agent writes to meta/counts/<backup>.json (backup-agent/agent.sh write_counts): row counts per table and
// the Keycloak user count per realm. Counts only.
public sealed record CountSnapshot(Dictionary<string, long>? Tables, Dictionary<string, long>? KeycloakUsers);

// design/specs/2026-11-05-backup-restore-design.md §4.2. Output never carries clinical text, file or patient names, or
// secrets: only counts and fixed reasons.
public sealed partial class RestoreChecker(
    string connectionString,
    string keycloakConnectionString,
    INoteFileStore store,
    ITimestampAuthority tsa,
    INoteFileStore? source = null,
    string? baselineConnectionString = null)
{
    public const int DefaultSample = 200;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    public async Task<RestoreCheckReport> RunAsync(CountSnapshot? snapshot, int sample, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var checks = new List<CheckResult>
        {
            await GuardedAsync("integrity", () => IntegrityAsync(snapshot, ct)),
            await GuardedAsync("row_counts", () => RowCountsAsync(snapshot, ct)),
            await GuardedAsync("files", () => FilesAsync(sample, ct)),
            await GuardedAsync("keycloak", () => KeycloakAsync(snapshot, ct)),
        };
        return new RestoreCheckReport(checks.All(c => c.Passed), checks, Math.Round(clock.Elapsed.TotalSeconds, 2));
    }

    // A check that cannot run fails with the exception type only: messages could carry connection details.
    private static async Task<CheckResult> GuardedAsync(string name, Func<Task<CheckResult>> check)
    {
        try
        {
            return await check();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new CheckResult(name, false, $"could not run: {e.GetType().Name}");
        }
    }

    private AnamnysDbContext Db() =>
        new(new DbContextOptionsBuilder<AnamnysDbContext>().UseNpgsql(connectionString).Options);

    private async Task<CheckResult> IntegrityAsync(CountSnapshot? snapshot, CancellationToken ct)
    {
        await using var db = Db();
        var providers = await db.Providers.AsNoTracking().Select(p => p.Id).ToListAsync(ct);
        if (providers.Count == 0)
        {
            return new CheckResult("integrity", false, "no providers found, nothing to verify");
        }
        // With a baseline (the live database, read-only) it must be readable before anything can count as pre-existing:
        // an unreadable baseline fails the check instead of hiding restore problems.
        await using var baseline = baselineConnectionString is null
            ? null
            : new AnamnysDbContext(new DbContextOptionsBuilder<AnamnysDbContext>().UseNpgsql(baselineConnectionString).Options);
        if (baseline is not null)
        {
            try
            {
                await baseline.Providers.AsNoTracking().AnyAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return new CheckResult("integrity", false, $"baseline database unreadable: {e.GetType().Name}");
            }
        }
        var added = new List<string>();
        var preExisting = new List<string>();
        long seals = 0;
        foreach (var provider in providers)
        {
            var chain = await IntegrityVerifier.VerifyChainAsync(db, provider, tsa, DateTimeOffset.UtcNow, ct);
            seals += chain.TotalSeals;
            if (chain.FirstProblem is null)
            {
                continue;
            }
            // Pre-existing only when the live chain has the same problem: the same kind at the same first-problem
            // sequence. A chain broken live in another way, or later, still counts as a restore problem.
            if (baseline is not null
                && (await IntegrityVerifier.VerifyChainAsync(baseline, provider, tsa, DateTimeOffset.UtcNow, ct)).FirstProblem is { } live
                && live.Kind == chain.FirstProblem.Kind && live.Sequence == chain.FirstProblem.Sequence)
            {
                preExisting.Add(chain.FirstProblem.Kind);
            }
            else
            {
                added.Add($"{chain.FirstProblem.Kind} at {chain.FirstProblem.Sequence}");
            }
        }
        if (added.Count == 0 && preExisting.Count == 0 && seals == 0 && snapshot?.Tables is not null
            && snapshot.Tables.TryGetValue("IntegritySeals", out var expectedSeals) && expectedSeals > 0)
        {
            return new CheckResult("integrity", false, $"no seals found, the snapshot has {expectedSeals}");
        }
        var baselineNote = baselineConnectionString is null
            ? ""
            : $"; {added.Count} new; {preExisting.Count} pre-existing{Kinds(preExisting)}";
        return added.Count == 0
            ? new CheckResult("integrity", true, $"{providers.Count} chains, {seals} seals checked{baselineNote}")
            : new CheckResult("integrity", false,
                $"{added.Count} of {providers.Count} chains with a problem ({string.Join(", ", added)}){baselineNote}");
    }

    private static string Kinds(List<string> kinds) =>
        kinds.Count == 0 ? "" : $" ({string.Join(", ", kinds.Distinct())})";

    private async Task<CheckResult> RowCountsAsync(CountSnapshot? snapshot, CancellationToken ct)
    {
        if (snapshot?.Tables is not { Count: > 0 })
        {
            return new CheckResult("row_counts", true, "skipped");
        }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var below = new List<string>();
        foreach (var (table, expected) in snapshot.Tables)
        {
            if (!Identifier().IsMatch(table))
            {
                below.Add("invalid table name in snapshot");
                continue;
            }
            long actual;
            try
            {
                await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", connection);
                actual = (long)(await command.ExecuteScalarAsync(ct))!;
            }
            catch (PostgresException)
            {
                below.Add($"{table}: missing table");
                continue;
            }
            if (actual < expected)
            {
                below.Add($"{table}: {actual} < {expected}");
            }
        }
        return below.Count == 0
            ? new CheckResult("row_counts", true, $"{snapshot.Tables.Count} tables at or above the snapshot")
            : new CheckResult("row_counts", false, $"below the snapshot: {string.Join("; ", below)}");
    }

    private async Task<CheckResult> FilesAsync(int sample, CancellationToken ct)
    {
        await using var db = Db();
        var pdfs = (await db.Notes.AsNoTracking().Where(n => n.PdfObjectKey != null && n.PdfSha256 != null)
                .OrderBy(_ => EF.Functions.Random()).Take(sample).Select(n => new { Key = n.PdfObjectKey!, Sha = n.PdfSha256! }).ToListAsync(ct))
            .Concat(await db.ClinicalDocuments.AsNoTracking().Where(d => d.PdfObjectKey != null && d.PdfSha256 != null)
                .OrderBy(_ => EF.Functions.Random()).Take(sample).Select(d => new { Key = d.PdfObjectKey!, Sha = d.PdfSha256! }).ToListAsync(ct))
            .ToList();
        var externals = await db.ExternalDocuments.AsNoTracking()
            .Where(d => d.ScanStatus == "clean" && d.ObjectKey != null && d.DeletedAt == null)
            .OrderBy(_ => EF.Functions.Random()).Take(sample).Select(d => new { Key = d.ObjectKey!, d.ByteSize }).ToListAsync(ct);

        var added = new Dictionary<string, int>();
        var preExisting = new Dictionary<string, int>();
        var withBaseline = baselineConnectionString is not null;
        const string SourceDiffers = "external document(s) different from the source";
        const string SourceUnreadable = "external document(s) not compared: source read failed";
        if (withBaseline)
        {
            // Baseline mode decides "pre-existing" from the source store, so the source must be there and readable: a
            // wrong endpoint, wrong credentials or a store that is down fail the check instead of passing it.
            if (source is null)
            {
                return new CheckResult("files", false, "baseline mode without a source store");
            }
            var probe = pdfs.Select(p => p.Key).Concat(externals.Select(e => e.Key)).FirstOrDefault();
            if (probe is not null && (await TryGetAsync(source, probe, ct)).Failed)
            {
                return new CheckResult("files", false, "source store unreadable");
            }
        }
        void Count(Dictionary<string, int> bucket, string kind) => bucket[kind] = bucket.GetValueOrDefault(kind) + 1;
        // Without a baseline every problem counts. With one, a problem counts only when the source still has the object
        // correct (the restore lost or damaged it); an object already missing or wrong in the source is pre-existing. A
        // source read that fails is never pre-existing: the object could not be compared, so the problem counts.
        async Task RecordAsync(string kind, string key, string? sha, long? size)
        {
            if (!withBaseline)
            {
                Count(added, kind);
                return;
            }
            switch (await SourceStateAsync(key, sha, size, ct))
            {
                case SourceState.Good: Count(added, kind); break;
                case SourceState.Unreadable: Count(added, $"{kind}, source read failed"); break;
                default: Count(preExisting, kind); break;
            }
        }

        foreach (var pdf in pdfs)
        {
            var (content, failed) = await TryGetAsync(store, pdf.Key, ct);
            if (failed) { await RecordAsync("object(s) unreadable", pdf.Key, pdf.Sha, null); }
            else if (content is null) { await RecordAsync("missing object(s)", pdf.Key, pdf.Sha, null); }
            else if (!string.Equals(Hex(content), pdf.Sha, StringComparison.OrdinalIgnoreCase)) { await RecordAsync("PDF(s) with a different SHA-256", pdf.Key, pdf.Sha, null); }
        }
        foreach (var external in externals)
        {
            var (content, failed) = await TryGetAsync(store, external.Key, ct);
            if (failed) { await RecordAsync("object(s) unreadable", external.Key, null, external.ByteSize); continue; }
            if (content is null) { await RecordAsync("missing object(s)", external.Key, null, external.ByteSize); continue; }
            if (content.LongLength != external.ByteSize) { await RecordAsync("external document(s) with a different size", external.Key, null, external.ByteSize); }
            if (source is not null)
            {
                var (original, originalFailed) = await TryGetAsync(source, external.Key, ct);
                if (originalFailed)
                {
                    // Never pre-existing: an unreadable source proves nothing either way.
                    Count(added, SourceUnreadable);
                }
                else if (original is null || Hex(original) != Hex(content))
                {
                    // With a baseline, a source that is missing or does not match the stored size cannot be the reference.
                    var sourceUsable = original is not null && original.LongLength == external.ByteSize;
                    Count(!withBaseline || sourceUsable ? added : preExisting, SourceDiffers);
                }
            }
        }
        var total = pdfs.Count + externals.Count;
        if (total == 0)
        {
            var candidates = await db.Notes.AnyAsync(x => x.PdfObjectKey != null && x.PdfSha256 != null, ct)
                || await db.ClinicalDocuments.AnyAsync(x => x.PdfObjectKey != null && x.PdfSha256 != null, ct)
                || await db.ExternalDocuments.AnyAsync(x => x.ScanStatus == "clean" && x.ObjectKey != null && x.DeletedAt == null, ct);
            return candidates
                ? new CheckResult("files", false, "files exist in the database but none was checked")
                : new CheckResult("files", true, "nenhum arquivo para verificar");
        }
        var baselineNote = withBaseline
            ? $"; {added.Values.Sum()} new; {preExisting.Values.Sum()} pre-existing ({(preExisting.Count == 0 ? "none" : string.Join(", ", preExisting.Select(p => $"{p.Value} {p.Key}")))})"
            : "";
        return added.Count == 0
            ? new CheckResult("files", true, $"{total} sampled objects checked ({pdfs.Count} PDFs, {externals.Count} external documents){baselineNote}")
            : new CheckResult("files", false, $"{string.Join("; ", added.Select(p => $"{p.Value} {p.Key}"))} of {total} sampled{baselineNote}");
    }

    private enum SourceState { Good, MissingOrWrong, Unreadable }

    // Good: the source object exists and is what the database says it should be (PDF hash, or the stored size).
    private async Task<SourceState> SourceStateAsync(string key, string? sha, long? size, CancellationToken ct)
    {
        var (content, failed) = await TryGetAsync(source!, key, ct);
        if (failed)
        {
            return SourceState.Unreadable;
        }
        if (content is null)
        {
            return SourceState.MissingOrWrong;
        }
        var good = sha is not null
            ? string.Equals(Hex(content), sha, StringComparison.OrdinalIgnoreCase)
            : content.LongLength == size;
        return good ? SourceState.Good : SourceState.MissingOrWrong;
    }

    private async Task<CheckResult> KeycloakAsync(CountSnapshot? snapshot, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(keycloakConnectionString);
        await connection.OpenAsync(ct);
        var users = new Dictionary<string, long>();
        await using (var command = new NpgsqlCommand(
            "SELECT r.name, (SELECT count(*) FROM user_entity u WHERE u.realm_id = r.id) FROM realm r WHERE r.name LIKE 'anamnys-%'", connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                users[reader.GetString(0)] = reader.GetInt64(1);
            }
        }
        if (users.Count == 0)
        {
            return new CheckResult("keycloak", false, "no anamnys realm found");
        }
        var total = users.Values.Sum();
        // Per realm (final fix wave): the snapshot records the user count of each realm, and every snapshot realm must be
        // present with at least that many users, so a loss in one realm cannot hide behind a gain in another.
        if (snapshot?.KeycloakUsers is { Count: > 0 })
        {
            var expected = snapshot.KeycloakUsers.Values.Sum();
            var below = snapshot.KeycloakUsers
                .Where(r => users.GetValueOrDefault(r.Key) < r.Value)
                .Select(r => users.TryGetValue(r.Key, out var actual) ? $"{r.Key}: {actual} < {r.Value}" : $"{r.Key}: realm missing")
                .ToList();
            return below.Count == 0
                ? new CheckResult("keycloak", true, $"{users.Count} realm(s), {total} users (snapshot {expected})")
                : new CheckResult("keycloak", false, $"users below the snapshot: {string.Join("; ", below)}");
        }
        return total > 0
            ? new CheckResult("keycloak", true, $"{users.Count} realm(s), {total} users")
            : new CheckResult("keycloak", false, "the realm has no users");
    }

    private static async Task<(byte[]? Content, bool Failed)> TryGetAsync(INoteFileStore from, string key, CancellationToken ct)
    {
        try
        {
            return (await from.GetAsync(key, ct), false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, true);
        }
    }

    private static string Hex(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
