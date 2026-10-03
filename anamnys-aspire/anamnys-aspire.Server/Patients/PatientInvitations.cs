using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Anamnys.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Patients;

public enum InviteOutcome { Sent, NotFound, Archived, AlreadyActive, MissingContactEmail, EmailNotConfigured, SendFailed }

public enum AcceptOutcome { Accepted, Invalid, EmailMismatch, AlreadyLinked }

public sealed record AcceptResult(AcceptOutcome Outcome, string? ProviderName = null);

public sealed record PortalStatus(string Status, DateTimeOffset? InvitationSentAt, DateTimeOffset? InvitationExpiresAt)
{
    public static readonly PortalStatus None = new("none", null, null);
    public static readonly PortalStatus Active = new("active", null, null);
}

public static class PatientInvitationTokens
{
    // 32 random bytes as base64url (43 characters). The token exists only in the emailed
    // link; the database stores its SHA-256, so a database leak yields no usable links.
    public static string NewToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class PatientInvitationMessage
{
    private static readonly TimeZoneInfo PracticeTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    // No clinical content: who invited, the link and the expiry date only.
    public static EmailMessage Compose(string toEmail, string firstName, string providerName, Uri link, DateTimeOffset expiresAt)
    {
        var until = TimeZoneInfo.ConvertTime(expiresAt, PracticeTimeZone).ToString("dd/MM/yyyy", PtBr);
        const string subject = "Seu convite para o portal de pacientes Anamnys";

        var text =
            $"Olá, {firstName}.\n\n" +
            $"{providerName} convidou você para acessar o portal de pacientes Anamnys. " +
            "Use o link abaixo para criar sua conta ou entrar com este e-mail.\n\n" +
            $"{link}\n\n" +
            $"O convite vale até {until}.\n";

        var html = HtmlEncoder.Default;
        var htmlBody =
            $"<p>Olá, {html.Encode(firstName)}.</p>" +
            $"<p>{html.Encode(providerName)} convidou você para acessar o portal de pacientes Anamnys. " +
            "Use o link abaixo para criar sua conta ou entrar com este e-mail.</p>" +
            $"<p><a href=\"{html.Encode(link.ToString())}\">Aceitar o convite</a></p>" +
            $"<p>O convite vale até {until}.</p>";

        return new EmailMessage(toEmail, firstName, subject, text, htmlBody);
    }
}

// Provider-side invitation lifecycle and patient-side acceptance. See
// design/specs/2026-10-03-portal-invitation-design.md §3–4. Records are always resolved
// with Id == patientId && ProviderId == providerId.
public static class PatientInvitations
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(7);

    public static async Task<InviteOutcome> InviteAsync(
        AnamnysDbContext db,
        Guid providerId,
        Guid patientId,
        IEmailSender? sender,
        Uri portalBaseUrl,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowUtc = now.ToUniversalTime();
        var patient = await db.Patients.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == patientId && p.ProviderId == providerId, cancellationToken);
        if (patient is null)
        {
            return InviteOutcome.NotFound;
        }
        if (patient.ArchivedAt is not null)
        {
            return InviteOutcome.Archived;
        }
        if (patient.AccountId is not null)
        {
            return InviteOutcome.AlreadyActive;
        }
        if (string.IsNullOrWhiteSpace(patient.ContactEmail))
        {
            return InviteOutcome.MissingContactEmail;
        }
        if (sender is null)
        {
            return InviteOutcome.EmailNotConfigured;
        }

        await RevokePendingAsync(db, patientId, nowUtc, cancellationToken);

        var token = PatientInvitationTokens.NewToken();
        var invitation = new PatientInvitation
        {
            Id = Guid.NewGuid(),
            PatientId = patientId,
            Email = patient.ContactEmail,
            TokenHash = PatientInvitationTokens.Hash(token),
            CreatedAt = nowUtc,
            ExpiresAt = nowUtc + Validity,
        };
        db.PatientInvitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);

        var providerName = await db.Providers.Where(p => p.Id == providerId).Select(p => p.Name).SingleAsync(cancellationToken);
        var link = new Uri($"{portalBaseUrl.ToString().TrimEnd('/')}/convite/{token}");
        try
        {
            await sender.SendAsync(
                PatientInvitationMessage.Compose(patient.ContactEmail, patient.FirstName, providerName, link, invitation.ExpiresAt),
                cancellationToken);
        }
        catch (EmailSendException)
        {
            // The provider must not believe an invitation went out; nothing stays pending.
            invitation.RevokedAt = nowUtc;
            await db.SaveChangesAsync(CancellationToken.None);
            return InviteOutcome.SendFailed;
        }

        return InviteOutcome.Sent;
    }

    public static async Task<bool> CancelAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId && p.ProviderId == providerId, cancellationToken))
        {
            return false;
        }
        await RevokePendingAsync(db, patientId, now.ToUniversalTime(), cancellationToken);
        return true;
    }

    // Unlinks the record from the account; both survive. The person stops seeing this
    // provider in the portal.
    public static async Task<bool> RemoveAccessAsync(
        AnamnysDbContext db, Guid providerId, Guid patientId, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.SingleOrDefaultAsync(p => p.Id == patientId && p.ProviderId == providerId, cancellationToken);
        if (patient is null)
        {
            return false;
        }
        if (patient.AccountId is not null)
        {
            patient.AccountId = null;
            patient.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public static async Task<AcceptResult> AcceptAsync(
        AnamnysDbContext db, Guid accountId, string token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nowUtc = now.ToUniversalTime();
        var hash = PatientInvitationTokens.Hash(token);
        var invitation = await db.PatientInvitations.SingleOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);

        // Re-submitted by the account that already accepted it (a reload of the acceptance
        // page): report the same success instead of "invalid". Any other account still
        // gets "invalid" — the token stays single use.
        if (invitation is { AcceptedAt: not null } && invitation.AcceptedAccountId == accountId)
        {
            var linkedProvider = await db.Patients
                .Where(p => p.Id == invitation.PatientId && p.AccountId == accountId)
                .Join(db.Providers, p => p.ProviderId, pr => pr.Id, (p, pr) => pr.Name)
                .SingleOrDefaultAsync(cancellationToken);
            return linkedProvider is null ? new AcceptResult(AcceptOutcome.Invalid) : new AcceptResult(AcceptOutcome.Accepted, linkedProvider);
        }

        if (invitation is null || invitation.RevokedAt is not null || invitation.AcceptedAt is not null || invitation.ExpiresAt <= nowUtc)
        {
            return new AcceptResult(AcceptOutcome.Invalid);
        }

        var account = await db.PatientAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null || account.DisabledAt is not null)
        {
            return new AcceptResult(AcceptOutcome.Invalid);
        }
        if (!string.Equals(account.Email.Trim(), invitation.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new AcceptResult(AcceptOutcome.EmailMismatch);
        }

        var patient = await db.Patients.SingleAsync(p => p.Id == invitation.PatientId, cancellationToken);
        if (patient.ArchivedAt is not null)
        {
            return new AcceptResult(AcceptOutcome.Invalid);
        }
        if (patient.AccountId is not null
            || await db.Patients.AnyAsync(p => p.AccountId == accountId && p.ProviderId == patient.ProviderId, cancellationToken))
        {
            return new AcceptResult(AcceptOutcome.AlreadyLinked);
        }

        patient.AccountId = accountId;
        patient.UpdatedAt = nowUtc;
        invitation.AcceptedAt = nowUtc;
        invitation.AcceptedAccountId = accountId;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Patients_Account_Provider_key: a concurrent acceptance linked this account to
            // another record of the same provider first.
            return new AcceptResult(AcceptOutcome.AlreadyLinked);
        }

        var providerName = await db.Providers.Where(p => p.Id == patient.ProviderId).Select(p => p.Name).SingleAsync(cancellationToken);
        return new AcceptResult(AcceptOutcome.Accepted, providerName);
    }

    // accountId is the record's AccountId, which the caller already has loaded.
    public static async Task<PortalStatus> StatusAsync(
        AnamnysDbContext db, Guid patientId, Guid? accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (accountId is not null)
        {
            return PortalStatus.Active;
        }

        var pending = await db.PatientInvitations.AsNoTracking()
            .Where(i => i.PatientId == patientId && i.AcceptedAt == null && i.RevokedAt == null)
            .Select(i => new { i.CreatedAt, i.ExpiresAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (pending is null)
        {
            return PortalStatus.None;
        }

        return new PortalStatus(pending.ExpiresAt > now.ToUniversalTime() ? "invited" : "expired", pending.CreatedAt, pending.ExpiresAt);
    }

    // Tracked, not ExecuteUpdate: a bulk update would leave any invitation this context
    // already tracks looking pending. Saved on its own, before any new invitation is
    // inserted, so the one-pending-per-record index never sees two at once.
    internal static async Task RevokePendingAsync(AnamnysDbContext db, Guid patientId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var pending = await db.PatientInvitations
            .Where(i => i.PatientId == patientId && i.AcceptedAt == null && i.RevokedAt == null)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }
        foreach (var invitation in pending)
        {
            invitation.RevokedAt = nowUtc;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
