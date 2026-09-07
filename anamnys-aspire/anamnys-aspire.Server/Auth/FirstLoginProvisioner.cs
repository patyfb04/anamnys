using System.Security.Claims;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Auth;

public static class AnamnysClaims
{
    public const string LocalId = "anamnys:lid";
}

public sealed class FirstLoginProvisioner(AnamnysDbContext db)
{
    private static readonly string[] StaffRoles = ["owner", "support", "ops"];

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
            Realms.Providers => await ProvisionProviderAsync(subject, email, name, cancellationToken),
            Realms.Owners => await ProvisionStaffAsync(principal, subject, email, name, cancellationToken),
            Realms.Patients => await ResolvePatientAsync(principal, subject, email, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown realm {realm}."),
        };
    }

    private async Task<Guid> ProvisionProviderAsync(Guid subject, string email, string name, CancellationToken cancellationToken)
    {
        var existing = await db.Providers
            .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var now = DateTimeOffset.UtcNow;
        var provider = new Provider
        {
            Id = Guid.NewGuid(),
            ExternalSubject = subject,
            Email = email,
            Name = name,
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

    private async Task<Guid> ProvisionStaffAsync(
        ClaimsPrincipal principal,
        Guid subject,
        string email,
        string name,
        CancellationToken cancellationToken)
    {
        var existing = await db.Staff
            .SingleOrDefaultAsync(s => s.ExternalSubject == subject, cancellationToken);
        if (existing is not null)
        {
            return existing.DisabledAt is null
                ? existing.Id
                : throw new InvalidOperationException("This staff account is disabled.");
        }

        // Realm membership alone confers no internal-staff role: a token that
        // authenticates against the owners realm but carries none of the
        // realm roles Staff_Role_ck allows is a misconfiguration, not a
        // signal to default to "support".
        var tokenRoles = principal.FindAll("roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var role = StaffRoles.FirstOrDefault(tokenRoles.Contains)
            ?? throw new InvalidOperationException(
                $"Token for {subject} carries none of the required staff roles ({string.Join(", ", StaffRoles)}).");

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

            return existing.DisabledAt is null
                ? existing.Id
                : throw new InvalidOperationException("This staff account is disabled.");
        }

        return staff.Id;
    }

    // Patients are never created here. A PatientAccount exists only because a
    // provider created the Patient row and invited them, so a login with no
    // matching account is an error, not a signal to create one.
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
            return bySubject.DisabledAt is null
                ? bySubject.Id
                : throw new InvalidOperationException("This patient account is disabled.");
        }

        // Binding an unclaimed PatientAccount to whichever subject presents
        // its email is only safe if Keycloak itself has verified that email
        // belongs to this subject — otherwise any account with a known email
        // and an unverified address at the same IdP can claim it.
        var emailVerified = string.Equals(
            principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        if (!emailVerified)
        {
            throw new InvalidOperationException("Cannot bind a patient account to an unverified email.");
        }

        var normalizedEmail = email.Trim().ToUpperInvariant();
        var byEmail = await db.PatientAccounts
            .SingleOrDefaultAsync(
                a => a.ExternalSubject == null && a.Email.ToUpper() == normalizedEmail,
                cancellationToken)
            ?? throw new InvalidOperationException("No patient account matches this login.");

        if (byEmail.DisabledAt is not null)
        {
            throw new InvalidOperationException("This patient account is disabled.");
        }

        byEmail.ExternalSubject = subject;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Someone else claimed the same unclaimed row (or the same
            // subject logged in twice concurrently) between our read and our
            // write. Re-read by subject: if it is now bound, that is success;
            // otherwise this login genuinely lost the race for an unclaimed
            // account and should fail rather than silently retry the bind.
            db.Entry(byEmail).State = EntityState.Detached;
            var afterRace = await db.PatientAccounts
                .SingleOrDefaultAsync(a => a.ExternalSubject == subject, cancellationToken)
                ?? throw new InvalidOperationException("No patient account matches this login.");

            return afterRace.DisabledAt is null
                ? afterRace.Id
                : throw new InvalidOperationException("This patient account is disabled.");
        }

        return byEmail.Id;
    }
}
