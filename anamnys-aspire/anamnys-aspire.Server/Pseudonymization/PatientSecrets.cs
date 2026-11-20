using System.Security.Cryptography;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Pseudonymization;

// The per-patient pseudonym secret (design/specs/2026-11-17-pseudonymization-design.md §4): 32 random bytes, stored only
// protected with ASP.NET Core Data Protection over the dedicated, Postgres-persisted key ring (PseudonymizationKeyRing).
// One read (and lock) per token assignment, i.e. per pseudonymisation call. The caller zeroes the returned bytes after use.
public sealed class PatientSecrets(IDataProtectionProvider dataProtection)
{
    public const string Purpose = "Anamnys.Pseudonymization.v1";
    public const int SecretBytes = 32;

    private readonly IDataProtector _protector = dataProtection.CreateProtector(Purpose);

    // Creates the secret when the patient has none, then reads it under a row lock that lasts until the caller's
    // transaction ends, so concurrent token assignments for one patient run one after the other. Must run inside a transaction.
    internal async Task<byte[]> LockAsync(AnamnysDbContext db, Guid patientId, CancellationToken ct)
    {
        // The usual case: the secret exists and is locked at once; a new one is made only when there is none.
        var existing = await SelectForUpdateAsync(db, patientId, ct);
        if (existing.Count == 1)
        {
            return Unprotect(existing[0]);
        }
        var fresh = RandomNumberGenerator.GetBytes(SecretBytes);
        byte[] protectedFresh;
        try
        {
            protectedFresh = _protector.Protect(fresh);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fresh);
        }
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "PatientPseudonymSecrets" ("PatientId","Protected") VALUES ({patientId}, {protectedFresh})
            ON CONFLICT ("PatientId") DO NOTHING
            """, ct);
        // A concurrent first use may have won the insert: either way the row read here is the one that counts.
        return Unprotect((await SelectForUpdateAsync(db, patientId, ct)).Single());
    }

    private static Task<List<byte[]>> SelectForUpdateAsync(AnamnysDbContext db, Guid patientId, CancellationToken ct) =>
        db.Database
            .SqlQuery<byte[]>($"""SELECT "Protected" AS "Value" FROM "PatientPseudonymSecrets" WHERE "PatientId" = {patientId} FOR UPDATE""")
            .ToListAsync(ct);

    private byte[] Unprotect(byte[] stored)
    {
        var secret = _protector.Unprotect(stored);
        if (secret.Length != SecretBytes)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new CryptographicException("The pseudonym secret has an unexpected length.");
        }
        return secret;
    }
}
