using Anamnys.Application.Common;
using Anamnys.Application.Interfaces;
using Anamnys.Domain.Entities;
using Anamnys.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Anamnys.Api.Controllers;

/// <summary>
/// Authenticated self-service account actions: change password, and TOTP two-factor
/// enrollment/verification/disable. Distinct from AuthController, which only handles the
/// unauthenticated login/register/logout surface.
/// </summary>
[ApiController]
[Route("api/account")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ITwoFactorService _twoFactor;

    public AccountController(AppDbContext db, ITwoFactorService twoFactor)
    {
        _db = db;
        _twoFactor = twoFactor;
    }

    /// <summary>Verifies the current password and, if it matches, sets a new one.</summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var provider = await _db.Providers.FindAsync(GetProviderId());
        if (provider == null) return NotFound();

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, provider.PasswordHash))
            return BadRequest(new { message = "Current password is incorrect." });

        var policyError = PasswordPolicy.Validate(request.NewPassword);
        if (policyError != null) return BadRequest(new { message = policyError });

        provider.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Whether 2FA is currently enabled for the authenticated provider.</summary>
    [HttpGet("2fa/status")]
    public async Task<ActionResult<TwoFactorStatusDto>> TwoFactorStatus()
    {
        var enabled = await _db.Providers
            .Where(p => p.Id == GetProviderId())
            .Select(p => p.TwoFactorEnabled)
            .FirstOrDefaultAsync();
        return Ok(new TwoFactorStatusDto(enabled));
    }

    /// <summary>
    /// Starts (or restarts) enrollment: generates a new secret and stores it encrypted, but
    /// does NOT enable 2FA yet — that only happens once /2fa/verify confirms the user's
    /// authenticator app actually has it (proving the QR/secret was captured correctly).
    /// </summary>
    [HttpPost("2fa/setup")]
    public async Task<ActionResult<TwoFactorSetupDto>> TwoFactorSetup()
    {
        var provider = await _db.Providers.FindAsync(GetProviderId());
        if (provider == null) return NotFound();

        var secret = _twoFactor.GenerateSecret();
        provider.TwoFactorSecretEncrypted = _twoFactor.Encrypt(secret);
        await _db.SaveChangesAsync();

        return Ok(new TwoFactorSetupDto(secret, _twoFactor.GetOtpauthUri(secret, provider.Email)));
    }

    /// <summary>
    /// Confirms enrollment with a code from the authenticator app, enables 2FA, and issues
    /// recovery codes — returned in plaintext exactly once; only their hashes are kept.
    /// </summary>
    [HttpPost("2fa/verify")]
    public async Task<ActionResult<TwoFactorVerifyResponse>> TwoFactorVerify([FromBody] TwoFactorCodeRequest request)
    {
        var provider = await _db.Providers.FindAsync(GetProviderId());
        if (provider?.TwoFactorSecretEncrypted == null)
            return BadRequest(new { message = "Call /2fa/setup first." });

        var secret = _twoFactor.Decrypt(provider.TwoFactorSecretEncrypted);
        if (!_twoFactor.ValidateCode(secret, request.Code))
            return BadRequest(new { message = "Invalid code." });

        provider.TwoFactorEnabled = true;

        // Replace any codes from a previous enrollment.
        var existing = await _db.RecoveryCodes.Where(c => c.ProviderId == provider.Id).ToListAsync();
        _db.RecoveryCodes.RemoveRange(existing);

        var plainCodes = _twoFactor.GenerateRecoveryCodes();
        foreach (var code in plainCodes)
        {
            _db.RecoveryCodes.Add(new RecoveryCode
            {
                ProviderId = provider.Id,
                CodeHash = _twoFactor.HashRecoveryCode(code),
            });
        }

        await _db.SaveChangesAsync();
        return Ok(new TwoFactorVerifyResponse(plainCodes.ToList()));
    }

    /// <summary>Disables 2FA — requires the current password plus a valid TOTP or recovery code.</summary>
    [HttpPost("2fa/disable")]
    public async Task<IActionResult> TwoFactorDisable([FromBody] TwoFactorDisableRequest request)
    {
        var provider = await _db.Providers.FindAsync(GetProviderId());
        if (provider == null) return NotFound();

        if (!BCrypt.Net.BCrypt.Verify(request.Password, provider.PasswordHash))
            return BadRequest(new { message = "Password is incorrect." });

        if (!await VerifyTotpOrRecoveryCode(provider, request.Code))
            return BadRequest(new { message = "Invalid code." });

        provider.TwoFactorEnabled = false;
        provider.TwoFactorSecretEncrypted = null;

        var codes = await _db.RecoveryCodes.Where(c => c.ProviderId == provider.Id).ToListAsync();
        _db.RecoveryCodes.RemoveRange(codes);

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>
    /// Tries the code as a TOTP first, then as an unused recovery code (marking it spent on
    /// success). Shared with AuthController's login-completion step via internal visibility.
    /// </summary>
    internal static async Task<bool> VerifyTotpOrRecoveryCode(
        Provider provider, string code, ITwoFactorService twoFactor, AppDbContext db)
    {
        if (provider.TwoFactorSecretEncrypted != null &&
            twoFactor.ValidateCode(twoFactor.Decrypt(provider.TwoFactorSecretEncrypted), code))
            return true;

        var unused = await db.RecoveryCodes
            .Where(c => c.ProviderId == provider.Id && c.UsedAt == null)
            .ToListAsync();

        var match = unused.FirstOrDefault(c => twoFactor.VerifyRecoveryCode(code, c.CodeHash));
        if (match == null) return false;

        match.UsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    private Task<bool> VerifyTotpOrRecoveryCode(Provider provider, string code) =>
        VerifyTotpOrRecoveryCode(provider, code, _twoFactor, _db);

    private Guid GetProviderId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record TwoFactorStatusDto(bool Enabled);
public record TwoFactorSetupDto(string Secret, string OtpauthUri);
public record TwoFactorCodeRequest(string Code);
public record TwoFactorVerifyResponse(List<string> RecoveryCodes);
public record TwoFactorDisableRequest(string Password, string Code);
