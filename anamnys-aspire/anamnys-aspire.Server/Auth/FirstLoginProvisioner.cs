using System.Security.Claims;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Auth;

public static class AnamnysClaims
{
    public const string LocalId = "anamnys:lid";
}

public static class StaffRoles
{
    public static readonly string[] All = ["owner", "support", "ops"];
}

public sealed class FirstLoginProvisioner(AnamnysDbContext db, ILogger<FirstLoginProvisioner> logger)
{
    public async Task<Guid> ProvisionAsync(
        ClaimsPrincipal principal,
        string realm,
        CancellationToken cancellationToken)
    {
        var subjectRaw = principal.FindFirstValue("sub")
            ?? throw new InvalidOperationException("Token has no sub claim.");
        var subject = Guid.Parse(subjectRaw);

        var email = principal.FindFirstValue("email")
            ?? throw new InvalidOperationException("Token has no email claim.");
        var name = principal.FindFirstValue("name") ?? email;

        return realm switch
        {
            Realms.Providers => await ProvisionProviderAsync(principal, subject, email, name, cancellationToken),
            Realms.Owners => await ProvisionStaffAsync(principal, subject, email, name, cancellationToken),
            Realms.Patients => await ResolvePatientAsync(principal, subject, email, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown realm {realm}."),
        };
    }

    private async Task<Guid> ProvisionProviderAsync(
        ClaimsPrincipal principal, Guid subject, string email, string name, CancellationToken cancellationToken)
    {
        var existing = await db.Providers
            .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken);
        if (existing is not null)
        {
            await SyncProviderEmailAsync(existing, principal, email, cancellationToken);
            return existing.Id;
        }

        // Required fields on the providers realm's registration form (see the realm's
        // declarative user profile config), so present for every self-registered
        // provider. Null here only for a provider created before that shipped — the
        // Providers_Crp_ck constraint accepts both-null as well as both-set.
        var crpNumber = principal.FindFirstValue("crpNumber");
        var crpRegion = principal.FindFirstValue("crpRegion");

        var now = DateTimeOffset.UtcNow;
        var provider = new Provider
        {
            Id = Guid.NewGuid(),
            ExternalSubject = subject,
            Email = email,
            Name = name,
            CrpNumber = crpNumber,
            CrpRegion = crpRegion,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Providers.Add(provider);

        // Two concurrent first logins for the same subject both miss the
        // SingleOrDefaultAsync above and both try to insert; the unique index
        // on ExternalSubject lets exactly one succeed. Rather than 500 the
        // loser, re-read: the winner's row is now there.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(provider).State = EntityState.Detached;
            existing = await db.Providers
                .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return existing.Id;
        }

        return provider.Id;
    }

    // Owners-realm registration is open, so realm membership alone confers nothing: a
    // token with none of the staff roles gets a pending row (Role null) and a session
    // that /api/admin refuses. Someone grants a role in the Keycloak console; the next
    // login's token carries it and this activates the row. Permissions are read from
    // the token, never from Role — Role is not the access gate — so a revoked role
    // lapses only when the session's token-derived claims do, at the next login.
    private async Task<Guid> ProvisionStaffAsync(
        ClaimsPrincipal principal,
        Guid subject,
        string email,
        string name,
        CancellationToken cancellationToken)
    {
        var tokenRoles = principal.FindAll("roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var role = StaffRoles.All.FirstOrDefault(tokenRoles.Contains);

        var existing = await db.Staff
            .SingleOrDefaultAsync(s => s.ExternalSubject == subject, cancellationToken);
        if (existing is not null)
        {
            return await ResolveExistingStaffAsync(existing, role, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var staff = new Staff
        {
            Id = Guid.NewGuid(),
            ExternalSubject = subject,
            Email = email,
            Name = name,
            Role = role,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Staff.Add(staff);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(staff).State = EntityState.Detached;
            existing = await db.Staff
                .SingleOrDefaultAsync(s => s.ExternalSubject == subject, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return await ResolveExistingStaffAsync(existing, role, cancellationToken);
        }

        return staff.Id;
    }

    private async Task<Guid> ResolveExistingStaffAsync(Staff existing, string? role, CancellationToken cancellationToken)
    {
        if (existing.DisabledAt is not null)
        {
            throw new InvalidOperationException("This staff account is disabled.");
        }

        if (role is not null && role != existing.Role)
        {
            existing.Role = role;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Staff {StaffId} role set from token.", existing.Id);
        }

        return existing.Id;
    }

    // A patient login resolves to a PatientAccount, never to a clinical record: records
    // belong to providers and are linked to an account only through Patient.AccountId
    // (design/specs/2026-10-02-patient-accounts-design.md). The returned id is the
    // account's, and becomes the patient realm's LocalId.
    private async Task<Guid> ResolvePatientAsync(
        ClaimsPrincipal principal,
        Guid subject,
        string email,
        CancellationToken cancellationToken)
    {
        var bySubject = await db.PatientAccounts
            .SingleOrDefaultAsync(a => a.ExternalSubject == subject, cancellationToken);
        if (bySubject is not null)
        {
            if (bySubject.DisabledAt is not null)
            {
                throw new InvalidOperationException("This patient account is disabled.");
            }

            await SyncPatientEmailAsync(bySubject, principal, email, cancellationToken);
            return bySubject.Id;
        }

        // A portal account's email must be one Keycloak has verified as this subject's.
        if (!IsEmailVerified(principal))
        {
            throw new InvalidOperationException("Cannot register a patient account with an unverified email.");
        }

        // First login for this subject: a self-registration. There is deliberately no "link
        // the record whose email matches" step: a provider's record carries a ContactEmail
        // for notices, and matching on it would attach any later portal sign-up to that
        // provider's record without an invitation (see
        // design/specs/2026-10-01-patient-records-design.md §8). given_name/family_name come
        // from the same profile scope the provider realm relies on for its name claim; a
        // missing claim is a realm-config gap to notice later, not a reason to fail the login
        // (mirrors how ProvisionProviderAsync tolerates missing crpNumber/crpRegion).
        var now = DateTimeOffset.UtcNow;
        var account = new PatientAccount
        {
            Id = Guid.NewGuid(),
            ExternalSubject = subject,
            Email = email,
            FirstName = principal.FindFirstValue("given_name") ?? "",
            LastName = principal.FindFirstValue("family_name") ?? "",
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.PatientAccounts.Add(account);

        // Two concurrent first logins for the same subject both miss the lookup above and
        // both try to insert; the unique index on ExternalSubject lets exactly one succeed.
        // Rather than 500 the loser, re-read: the winner's row is now there.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(account).State = EntityState.Detached;
            var afterCreateRace = await db.PatientAccounts
                .SingleOrDefaultAsync(a => a.ExternalSubject == subject, cancellationToken);
            if (afterCreateRace is null)
            {
                // A genuine same-subject race would always succeed on the re-query above —
                // reaching here means the constraint that fired was the account email's
                // uniqueness: this email already belongs to another account.
                throw new InvalidOperationException(
                    "Cannot self-register: an account with this email already exists.");
            }

            return afterCreateRace.Id;
        }

        return account.Id;
    }

    // Keycloak owns the login email; it changes through its UPDATE_EMAIL action, which
    // only applies the new address once the user confirms it. A returning user's row
    // follows it here. A clash with another row of the same realm (an account deleted and
    // re-created in Keycloak, say) keeps the old value: the login itself must never fail
    // over this. Logged by row id only — an email address is PHI.
    private async Task SyncProviderEmailAsync(
        Provider row, ClaimsPrincipal principal, string email, CancellationToken cancellationToken)
    {
        if (!NeedsEmailSync(row.Email, principal, email))
        {
            return;
        }

        var normalized = email.Trim().ToUpperInvariant();
        if (await db.Providers.AnyAsync(p => p.Id != row.Id && p.Email.ToUpper() == normalized, cancellationToken))
        {
            logger.LogWarning("Email sync skipped for provider {ProviderId}: another provider holds that email.", row.Id);
            return;
        }

        row.Email = email;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveEmailSyncAsync(row, "provider", row.Id, cancellationToken);
    }

    private async Task SyncPatientEmailAsync(
        PatientAccount row, ClaimsPrincipal principal, string email, CancellationToken cancellationToken)
    {
        if (!NeedsEmailSync(row.Email, principal, email))
        {
            return;
        }

        var normalized = email.Trim().ToUpperInvariant();
        if (await db.PatientAccounts.AnyAsync(a => a.Id != row.Id && a.Email.ToUpper() == normalized, cancellationToken))
        {
            logger.LogWarning("Email sync skipped for patient account {AccountId}: another account holds that email.", row.Id);
            return;
        }

        row.Email = email;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveEmailSyncAsync(row, "patient", row.Id, cancellationToken);
    }

    private async Task SaveEmailSyncAsync(object row, string kind, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent write took the address between the check above and this save.
            await db.Entry(row).ReloadAsync(cancellationToken);
            logger.LogWarning("Email sync skipped for {Kind} {Id}: the email was taken concurrently.", kind, id);
        }
    }

    private static bool NeedsEmailSync(string? stored, ClaimsPrincipal principal, string email) =>
        IsEmailVerified(principal) && !string.Equals(stored, email, StringComparison.OrdinalIgnoreCase);

    private static bool IsEmailVerified(ClaimsPrincipal principal) =>
        string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
}
