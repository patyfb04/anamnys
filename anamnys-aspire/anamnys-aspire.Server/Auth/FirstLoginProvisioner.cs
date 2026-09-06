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
            Realms.Owners => await ProvisionStaffAsync(subject, email, name, cancellationToken),
            Realms.Patients => await ResolvePatientAsync(subject, email, cancellationToken),
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
        await db.SaveChangesAsync(cancellationToken);
        return provider.Id;
    }

    private async Task<Guid> ProvisionStaffAsync(Guid subject, string email, string name, CancellationToken cancellationToken)
    {
        var existing = await db.Staff
            .SingleOrDefaultAsync(s => s.ExternalSubject == subject, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var now = DateTimeOffset.UtcNow;
        var staff = new Staff
        {
            Id = Guid.NewGuid(),
            ExternalSubject = subject,
            Email = email,
            Name = name,
            Role = "support",
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Staff.Add(staff);
        await db.SaveChangesAsync(cancellationToken);
        return staff.Id;
    }

    // Patients are never created here. A PatientAccount exists only because a
    // provider created the Patient row and invited them, so a login with no
    // matching account is an error, not a signal to create one.
    private async Task<Guid> ResolvePatientAsync(Guid subject, string email, CancellationToken cancellationToken)
    {
        var bySubject = await db.PatientAccounts
            .SingleOrDefaultAsync(a => a.ExternalSubject == subject, cancellationToken);
        if (bySubject is not null)
        {
            return bySubject.Id;
        }

        var byEmail = await db.PatientAccounts
            .SingleOrDefaultAsync(a => a.Email == email && a.ExternalSubject == null, cancellationToken)
            ?? throw new InvalidOperationException("No patient account matches this login.");

        byEmail.ExternalSubject = subject;
        await db.SaveChangesAsync(cancellationToken);
        return byEmail.Id;
    }
}
