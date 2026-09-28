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

    // A Patients row can exist two ways: a provider already created and invited this
    // patient (bind-by-subject or bind-by-unclaimed-email below), or nobody has —
    // in which case this login is a genuine self-registration and falls through to the
    // create branch at the end.
    private async Task<Guid> ResolvePatientAsync(
        ClaimsPrincipal principal,
        Guid subject,
        string email,
        CancellationToken cancellationToken)
    {
        var bySubject = await db.Patients
            .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken);
        if (bySubject is not null)
        {
            if (bySubject.DisabledAt is not null)
            {
                throw new InvalidOperationException("This patient account is disabled.");
            }

            await SyncPatientEmailAsync(bySubject, principal, email, cancellationToken);
            return bySubject.Id;
        }

        // Binding an unclaimed Patients row to whichever subject presents its email is
        // only safe if Keycloak itself has verified that email belongs to this subject —
        // otherwise any account with a known email and an unverified address at the same
        // IdP could claim it.
        if (!IsEmailVerified(principal))
        {
            throw new InvalidOperationException("Cannot bind a patient account to an unverified email.");
        }

        var normalizedEmail = email.Trim().ToUpperInvariant();
        var byEmail = await db.Patients
            .SingleOrDefaultAsync(
                p => p.ExternalSubject == null && p.Email != null && p.Email.ToUpper() == normalizedEmail,
                cancellationToken);

        if (byEmail is not null)
        {
            if (byEmail.DisabledAt is not null)
            {
                throw new InvalidOperationException("This patient account is disabled.");
            }

            byEmail.ExternalSubject = subject;
            byEmail.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Someone else claimed the same unclaimed row (or the same subject logged
                // in twice concurrently) between our read and our write. Re-read by
                // subject: if it is now bound, that is success; otherwise this login
                // genuinely lost the race for an unclaimed row and should fail rather
                // than silently retry.
                db.Entry(byEmail).State = EntityState.Detached;
                var afterBindRace = await db.Patients
                    .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken)
                    ?? throw new InvalidOperationException("No patient account matches this login.");

                return afterBindRace.DisabledAt is null
                    ? afterBindRace.Id
                    : throw new InvalidOperationException("This patient account is disabled.");
            }

            return byEmail.Id;
        }

        // Nobody invited this patient — this is a genuine self-registration. No provider
        // relationship exists yet (ProviderId stays null until some future flow, e.g.
        // booking a first appointment, sets it). given_name/family_name come from the same
        // profile scope the provider realm already relies on for its own name claim; a
        // missing claim here is a realm-config gap to notice later, not a reason to fail
        // the login (mirrors how ProvisionProviderAsync tolerates missing crpNumber/
        // crpRegion).
        var givenName = principal.FindFirstValue("given_name") ?? "";
        var familyName = principal.FindFirstValue("family_name") ?? "";

        var now = DateTimeOffset.UtcNow;
        var patient = new Patient
        {
            Id = Guid.NewGuid(),
            ExternalSubject = subject,
            Email = email,
            FirstName = givenName,
            LastName = familyName,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Patients.Add(patient);

        // Two concurrent first logins for the same subject both miss every lookup above
        // and both try to insert; the unique index on ExternalSubject lets exactly one
        // succeed. Rather than 500 the loser, re-read: the winner's row is now there.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(patient).State = EntityState.Detached;
            var afterCreateRace = await db.Patients
                .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken);
            if (afterCreateRace is null)
            {
                // A genuine same-subject race would always succeed on the re-query above
                // (the winning insert used this same ExternalSubject) — reaching here means
                // the actual constraint that fired was Patients.Email's uniqueness, not
                // ExternalSubject's, i.e. this email already belongs to some other row.
                throw new InvalidOperationException(
                    "Cannot self-register: an account with this email already exists.");
            }

            return afterCreateRace.Id;
        }

        return patient.Id;
    }

    // Keycloak owns the login email; it changes through its UPDATE_EMAIL action, which
    // only applies the new address once the user confirms it. A returning user's row
    // follows it here. A clash with another row (in practice an unclaimed patient invite
    // holding that address) keeps the old value: the login itself must never fail over
    // this. Logged by row id only — an email address is PHI.
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
        Patient row, ClaimsPrincipal principal, string email, CancellationToken cancellationToken)
    {
        if (!NeedsEmailSync(row.Email, principal, email))
        {
            return;
        }

        var normalized = email.Trim().ToUpperInvariant();
        if (await db.Patients.AnyAsync(
                p => p.Id != row.Id && p.Email != null && p.Email.ToUpper() == normalized, cancellationToken))
        {
            logger.LogWarning("Email sync skipped for patient {PatientId}: another patient row holds that email.", row.Id);
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
