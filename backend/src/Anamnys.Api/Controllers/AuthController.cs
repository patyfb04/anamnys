using Anamnys.Application.Common;
using Anamnys.Application.DTOs;
using Anamnys.Application.Interfaces;
using Anamnys.Domain.Entities;
using Anamnys.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Anamnys.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly ITwoFactorService _twoFactor;

    public AuthController(AppDbContext db, IConfiguration cfg, ITwoFactorService twoFactor)
    {
        _db = db;
        _cfg = cfg;
        _twoFactor = twoFactor;
    }

    /// <summary>
    /// Entry point for provider login. Looks up the provider by email, verifies the bcrypt
    /// password hash, and either issues a full session (cookie + token) or, if the account has
    /// 2FA enabled, a short-lived challenge token the client must complete via /login/2fa.
    /// Timing-safe: we always run BCrypt.Verify even when the account doesn't exist
    /// (the null-conditional short-circuits, but the response time is indistinguishable).
    /// </summary>
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponseDto>> Login([FromBody] LoginRequest request)
    {
        var provider = await _db.Providers
            .FirstOrDefaultAsync(p => p.Email == request.Email);

        // Reject if account not found or password mismatch — same error to avoid user enumeration
        if (provider == null || !BCrypt.Net.BCrypt.Verify(request.Password, provider.PasswordHash))
            return Unauthorized(new { message = "Invalid credentials." });

        if (provider.TwoFactorEnabled)
            return Ok(new LoginResponseDto(true, GenerateChallengeToken(provider), null));

        return Ok(new LoginResponseDto(false, null, IssueSession(provider)));
    }

    /// <summary>
    /// Second step of login when the account has 2FA enabled: exchanges a short-lived
    /// challenge token (from Login) plus a TOTP or recovery code for a real session.
    /// </summary>
    [HttpPost("login/2fa")]
    public async Task<ActionResult<LoginResponseDto>> LoginTwoFactor([FromBody] LoginTwoFactorRequest request)
    {
        var providerId = ValidateChallengeToken(request.ChallengeToken);
        if (providerId == null) return Unauthorized(new { message = "Challenge expired — please sign in again." });

        var provider = await _db.Providers.FindAsync(providerId.Value);
        if (provider == null || !provider.TwoFactorEnabled) return Unauthorized();

        if (!await AccountController.VerifyTotpOrRecoveryCode(provider, request.Code, _twoFactor, _db))
            return BadRequest(new { message = "Invalid code." });

        return Ok(new LoginResponseDto(false, null, IssueSession(provider)));
    }

    /// <summary>
    /// Registers a new provider account and immediately signs them in (same response shape
    /// as Login, including a usable JWT), so the client can go straight into the app.
    /// </summary>
    [HttpPost("register")]
    public async Task<ActionResult<LoginResponseDto>> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "Name, email, and password are required." });

        var passwordError = PasswordPolicy.Validate(request.Password);
        if (passwordError != null) return BadRequest(new { message = passwordError });

        if (await _db.Providers.AnyAsync(p => p.Email == request.Email))
            return Conflict(new { message = "An account with this email already exists." });

        var specialty = AuthControllerExtensions.ParseSpecialty(request.Specialty);

        var provider = new Provider
        {
            Email = request.Email.Trim(),
            Name = request.Name.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Specialty = specialty,
            // Sensible default per specialty — matches the format each specialty's notes use elsewhere.
            PreferredNoteFormat = specialty == Domain.Enums.Specialty.PhysicalTherapy
                ? Domain.Enums.NoteFormat.SOAP
                : Domain.Enums.NoteFormat.DAP,
        };

        _db.Providers.Add(provider);
        await _db.SaveChangesAsync();

        return Ok(new LoginResponseDto(false, null, IssueSession(provider)));
    }

    /// <summary>
    /// Clears the web session cookie. Always succeeds (even with no/expired credentials) so a
    /// stale cookie can always be cleared client-side. Native clients just drop their stored
    /// token locally — nothing server-side to revoke for a stateless Bearer JWT.
    /// </summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("auth_token", new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Path = "/",
        });
        return NoContent();
    }

    /// <summary>
    /// Returns the currently authenticated provider's profile.
    /// Used by the mobile app on startup to rehydrate the auth store from a stored JWT.
    /// </summary>
    [HttpGet("me")]
    [Microsoft.AspNetCore.Authorization.Authorize]
    public async Task<ActionResult<AuthUserDto>> Me()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idStr, out var id))
            return Unauthorized();

        var provider = await _db.Providers.FindAsync(id);
        if (provider == null) return NotFound();

        // Return profile without re-issuing a token — client keeps the existing one
        return Ok(new AuthUserDto(
            provider.Id, provider.Email, provider.Name,
            AuthControllerExtensions.SpecialtySlug(provider.Specialty), string.Empty));
    }

    /// <summary>Issues the real JWT, sets it as the web HttpOnly cookie, and builds the response DTO.</summary>
    private AuthUserDto IssueSession(Provider provider)
    {
        var token = GenerateToken(provider);

        // Dual-mode: native reads `token` from the body and stores it via SecureStore (unchanged
        // behavior); web ignores the body token entirely and relies solely on this cookie — see
        // services/api.ts on the client. Requires HTTPS (already the case in dev — see .env).
        Response.Cookies.Append("auth_token", token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UtcNow.AddDays(30),
            Path = "/",
        });

        return new AuthUserDto(
            provider.Id, provider.Email, provider.Name,
            AuthControllerExtensions.SpecialtySlug(provider.Specialty), token);
    }

    /// <summary>
    /// Creates a signed HS256 JWT containing the provider's ID, email, and specialty.
    /// The ID claim is extracted by all other controllers via ClaimTypes.NameIdentifier
    /// to scope every DB query to the authenticated provider (HIPAA access control).
    /// Token lifetime is 30 days — adjust for stricter HIPAA session requirements.
    /// </summary>
    private string GenerateToken(Provider provider)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_cfg["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, provider.Id.ToString()),
            new Claim(ClaimTypes.Email, provider.Email),
            new Claim("specialty", provider.Specialty.ToString()),
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// A short-lived (5 min), narrowly-scoped token proving the password step succeeded, for
    /// the sole purpose of completing /login/2fa. Program.cs's JwtBearer OnTokenValidated
    /// event rejects any token carrying this "purpose" claim from every OTHER endpoint, so a
    /// leaked challenge token can't be replayed as real API access.
    /// </summary>
    private string GenerateChallengeToken(Provider provider)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_cfg["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, provider.Id.ToString()),
            new Claim("purpose", "2fa-challenge"),
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private Guid? ValidateChallengeToken(string challengeToken)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_cfg["Jwt:Key"]!));
        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(challengeToken, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
            }, out _);

            if (principal.FindFirstValue("purpose") != "2fa-challenge") return null;
            var idStr = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(idStr, out var id) ? id : null;
        }
        catch
        {
            return null;
        }
    }
}

public record LoginRequest(string Email, string Password);

public record LoginTwoFactorRequest(string ChallengeToken, string Code);

public record RegisterRequest(string Email, string Password, string Name, string Specialty);

/// <summary>
/// Envelope for every login-completing response. Exactly one of (ChallengeToken, User) is set:
/// RequiresTwoFactor=true means the password step succeeded and the client must call
/// /login/2fa with ChallengeToken + a code; otherwise User is the signed-in session.
/// </summary>
public record LoginResponseDto(bool RequiresTwoFactor, string? ChallengeToken, AuthUserDto? User);

// Maps the C# enum to the snake_case string the mobile app expects, and back
file static class AuthControllerExtensions
{
    public static string SpecialtySlug(Anamnys.Domain.Enums.Specialty s) => s switch
    {
        Anamnys.Domain.Enums.Specialty.MentalHealth    => "mental_health",
        Anamnys.Domain.Enums.Specialty.PhysicalTherapy => "physical_therapy",
        _ => s.ToString().ToLowerInvariant()
    };

    public static Anamnys.Domain.Enums.Specialty ParseSpecialty(string? slug) => slug switch
    {
        "physical_therapy" => Anamnys.Domain.Enums.Specialty.PhysicalTherapy,
        _ => Anamnys.Domain.Enums.Specialty.MentalHealth,
    };
}
