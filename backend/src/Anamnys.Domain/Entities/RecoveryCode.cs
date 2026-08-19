namespace Anamnys.Domain.Entities;

/// <summary>
/// A single one-time 2FA recovery code, issued 10-at-a-time when a provider completes 2FA
/// enrollment (see ITwoFactorService.GenerateRecoveryCodes). Stored hashed (BCrypt, same as
/// passwords) — the plaintext is shown to the provider exactly once and never persisted.
/// Each code is independently invalidated via UsedAt rather than sharing one flag/array, so a
/// partially-used code set can't be replayed.
/// </summary>
public class RecoveryCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProviderId { get; set; }
    public Provider? Provider { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UsedAt { get; set; }
}
