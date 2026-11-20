using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Anamnys.Server.Pseudonymization;

// Stable per-patient tokens (design/specs/2026-11-17-pseudonymization-design.md §4): [P1], [Empresa 1], [Escola 1] and
// the unnumbered [Endereço]. A name is known only by fingerprint = HMAC-SHA256(patient secret, kind + ":" + normalize(name));
// numbers go per patient and kind in order of first appearance. Nothing maps a number back to a name.
public static class PseudonymTokens
{
    private const int Attempts = 3;

    // Trimmed, lower-case, accents removed, inner whitespace collapsed.
    public static string Normalize(string surface)
    {
        var collapsed = string.Join(' ', surface.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(collapsed.Length);
        foreach (var c in collapsed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    public static byte[] Fingerprint(ReadOnlySpan<byte> secret, string kind, string surface)
    {
        var message = Encoding.UTF8.GetBytes($"{kind}:{Normalize(surface)}");
        try
        {
            return HMACSHA256.HashData(secret, message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
        }
    }

    public static string Label(string kind, int number) => kind switch
    {
        EntityKinds.Person => $"[P{number}]",
        EntityKinds.Organization => $"[Empresa {number}]",
        EntityKinds.School => $"[Escola {number}]",
        EntityKinds.Address => "[Endereço]",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // The number of each entity, aligned with the input (0 for an address, which is never numbered). New names get the
    // next numbers of their kind in input order (the caller passes them in order of appearance). One transaction under
    // the lock of the patient's secret row; a unique violation from a concurrent writer is retried.
    public static async Task<IReadOnlyList<int>> NumbersAsync(
        AnamnysDbContext db, PatientSecrets secrets, Guid patientId, IReadOnlyList<(string Kind, string Surface)> entities,
        DateTimeOffset now, CancellationToken ct)
    {
        if (entities.Count == 0)
        {
            return [];
        }
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AttemptAsync(db, secrets, patientId, entities, now, ct);
            }
            catch (DbUpdateException e) when (attempt < Attempts && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private static async Task<IReadOnlyList<int>> AttemptAsync(
        AnamnysDbContext db, PatientSecrets secrets, Guid patientId, IReadOnlyList<(string Kind, string Surface)> entities,
        DateTimeOffset now, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var secret = await secrets.LockAsync(db, patientId, ct);
            List<string?> keys;
            try
            {
                keys = [.. entities.Select(e => e.Kind == EntityKinds.Address ? null : e.Kind + ":" + Convert.ToHexString(Fingerprint(secret, e.Kind, e.Surface)))];
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }

            var existing = await db.PseudonymTokens.AsNoTracking()
                .Where(t => t.PatientId == patientId)
                .Select(t => new { t.Kind, t.Fingerprint, t.Number })
                .ToListAsync(ct);
            var known = existing.ToDictionary(t => t.Kind + ":" + Convert.ToHexString(t.Fingerprint), t => t.Number);
            var next = existing.GroupBy(t => t.Kind).ToDictionary(g => g.Key, g => g.Max(t => t.Number));
            var numbers = new List<int>(entities.Count);
            for (var i = 0; i < entities.Count; i++)
            {
                if (keys[i] is not { } key)
                {
                    numbers.Add(0);
                    continue;
                }
                if (!known.TryGetValue(key, out var number))
                {
                    var kind = entities[i].Kind;
                    number = next.GetValueOrDefault(kind) + 1;
                    next[kind] = number;
                    known[key] = number;
                    db.PseudonymTokens.Add(new PseudonymToken
                    {
                        PatientId = patientId,
                        Kind = kind,
                        Fingerprint = Convert.FromHexString(key[(kind.Length + 1)..]),
                        Number = number,
                        CreatedAt = now.ToUniversalTime(),
                    });
                }
                numbers.Add(number);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (IReadOnlyList<int>)numbers;
        });
    }
}
