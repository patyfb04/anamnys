using System.Security.Cryptography;
using Anamnys.Application.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using OtpNet;

namespace Anamnys.Infrastructure.Services;

/// <summary>
/// TOTP via Otp.NET (RFC 6238, 30s step, 6 digits — the Google Authenticator/Authy standard),
/// secrets encrypted at rest via ASP.NET Core's Data Protection API, recovery codes hashed
/// with BCrypt exactly like account passwords.
/// </summary>
public class TwoFactorService : ITwoFactorService
{
    private const string Issuer = "Anamnys AI";
    private readonly IDataProtector _protector;

    public TwoFactorService(IDataProtectionProvider dataProtectionProvider)
    {
        // Purpose string scopes this protector so a key compromise/rotation elsewhere in the
        // app can't be used to decrypt 2FA secrets, and vice versa.
        _protector = dataProtectionProvider.CreateProtector("Anamnys.TwoFactorSecret.v1");
    }

    public string GenerateSecret()
    {
        var key = KeyGeneration.GenerateRandomKey(20); // 160 bits, the RFC 6238 recommendation
        return Base32Encoding.ToString(key);
    }

    public string GetOtpauthUri(string secret, string accountEmail)
    {
        var label = Uri.EscapeDataString($"{Issuer}:{accountEmail}");
        var issuer = Uri.EscapeDataString(Issuer);
        return $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&digits=6&period=30";
    }

    public bool ValidateCode(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        // One-step window each side absorbs clock drift between the server and the user's
        // device without materially widening the guessable window (each step is 30s).
        return totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(1, 1));
    }

    public string Encrypt(string secret) => _protector.Protect(secret);

    public string Decrypt(string encryptedSecret) => _protector.Unprotect(encryptedSecret);

    public IReadOnlyList<string> GenerateRecoveryCodes(int count = 10)
    {
        // XXXX-XXXX from a restricted alphabet (no 0/O/1/I) so codes stay easy to transcribe.
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[9]; // reused across iterations — one stack slot, not one per code
        var codes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            for (var j = 0; j < 9; j++)
            {
                if (j == 4) { chars[j] = '-'; continue; }
                chars[j] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
            }
            codes.Add(new string(chars));
        }
        return codes;
    }

    public string HashRecoveryCode(string code) => BCrypt.Net.BCrypt.HashPassword(Normalize(code));

    public bool VerifyRecoveryCode(string code, string hash) => BCrypt.Net.BCrypt.Verify(Normalize(code), hash);

    private static string Normalize(string code) => code.Trim().ToUpperInvariant();
}
