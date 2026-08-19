namespace Anamnys.Application.Interfaces;

/// <summary>
/// TOTP-based two-factor authentication: secret generation/encryption, code verification, and
/// one-time recovery codes (issued once at enrollment, hashed thereafter — same treatment as
/// account passwords).
/// </summary>
public interface ITwoFactorService
{
    /// <summary>Generates a new base32 TOTP secret (not yet persisted).</summary>
    string GenerateSecret();

    /// <summary>Builds the otpauth:// URI an authenticator app renders as a QR code.</summary>
    string GetOtpauthUri(string secret, string accountEmail);

    /// <summary>Validates a 6-digit code against a plaintext secret, allowing for clock drift.</summary>
    bool ValidateCode(string secret, string code);

    /// <summary>Encrypts a secret for storage (Provider.TwoFactorSecretEncrypted).</summary>
    string Encrypt(string secret);

    /// <summary>Decrypts a secret previously encrypted via <see cref="Encrypt"/>.</summary>
    string Decrypt(string encryptedSecret);

    /// <summary>Generates a fresh batch of plaintext one-time recovery codes.</summary>
    IReadOnlyList<string> GenerateRecoveryCodes(int count = 10);

    /// <summary>Hashes a recovery code for storage (BCrypt, same as passwords).</summary>
    string HashRecoveryCode(string code);

    /// <summary>Verifies a plaintext recovery code against its stored hash.</summary>
    bool VerifyRecoveryCode(string code, string hash);
}
