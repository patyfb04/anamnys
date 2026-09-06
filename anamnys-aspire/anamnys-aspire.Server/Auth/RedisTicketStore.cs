using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace Anamnys.Server.Auth;

// Keeps access and refresh tokens server-side. The browser holds only the
// opaque key, so an XSS bug has nothing to exfiltrate and the cookie stays
// well clear of the 4KB limit once realm roles are in the token.
public sealed class RedisTicketStore(
    IConnectionMultiplexer redis,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<RedisTicketStore> logger) : ITicketStore
{
    private const string KeyPrefix = "auth:ticket:";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Anamnys.TicketStore");

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = KeyPrefix + Guid.NewGuid().ToString("N");
        await RenewAsync(key, ticket);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var bytes = TicketSerializer.Default.Serialize(ticket);
        var protectedBytes = _protector.Protect(bytes);

        var expiresUtc = ticket.Properties.ExpiresUtc;
        var ttl = expiresUtc.HasValue
            ? expiresUtc.Value - DateTimeOffset.UtcNow
            : TimeSpan.FromHours(10);

        if (ttl <= TimeSpan.Zero)
        {
            ttl = TimeSpan.FromMinutes(1);
        }

        await redis.GetDatabase().StringSetAsync(key, protectedBytes, ttl);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var value = await redis.GetDatabase().StringGetAsync(key);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            var bytes = _protector.Unprotect((byte[])value!);
            return TicketSerializer.Default.Deserialize(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or EndOfStreamException or IOException)
        {
            // Key rotation, a tampered value, or a malformed payload —
            // TicketSerializer.Deserialize can throw any of these on bytes
            // that decrypt successfully but don't parse as a ticket. Treat all
            // of them as no session rather than failing the request with a
            // 500 — the caller is challenged and logs in again.
            logger.LogWarning(ex, "Failed to retrieve auth ticket {Key}; treating as no session.", key);
            return null;
        }
    }

    public Task RemoveAsync(string key) => redis.GetDatabase().KeyDeleteAsync(key);
}
