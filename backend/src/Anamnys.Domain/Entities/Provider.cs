using Anamnys.Domain.Enums;

namespace Anamnys.Domain.Entities;

public class Provider
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public Specialty Specialty { get; set; }
    public NoteFormat PreferredNoteFormat { get; set; }

    /// <summary>
    /// Determines which country/regional billing engine is used for this provider.
    /// Defaults to US CPT for backward compatibility.
    /// </summary>
    public BillingSystem BillingSystem { get; set; } = BillingSystem.UsCpt;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // ─── Two-factor authentication (TOTP) ──────────────────────────────────────
    // TwoFactorSecretEncrypted holds the base32 TOTP secret encrypted at rest via
    // ASP.NET Core's IDataProtector (see ITwoFactorService). It's populated as soon as
    // /account/2fa/setup is called (pending enrollment) and only takes effect for login once
    // TwoFactorEnabled flips to true via /account/2fa/verify.
    public bool TwoFactorEnabled { get; set; }
    public string? TwoFactorSecretEncrypted { get; set; }

    public List<RecoveryCode> RecoveryCodes { get; set; } = new();
}
