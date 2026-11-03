using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Konscious.Security.Cryptography;

namespace Anamnys.Server.Sharing;

// The token is 256 bits from the CSPRNG, URL-safe, kept only as its SHA-256. The password is generated here, shown once
// and kept only as Argon2id, with the parameters inside the stored string so they can be raised for new links later.
// Argon2id parameters: OWASP Password Storage Cheat Sheet's first configuration (m = 19 MiB, t = 2, p = 1).
public static class ShareSecrets
{
    public const int TokenBytes = 32;
    public const int PasswordLength = 16;
    public const string PasswordAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    internal const int MemoryKib = 19_456;
    internal const int Iterations = 2;
    internal const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MaxPasswordChars = 256;

    private static readonly Lazy<Task<string>> Dummy = new(() => HashPasswordAsync(NewPassword(), CancellationToken.None));

    public static bool IsDummyReady => Dummy.IsValueCreated && Dummy.Value.IsCompletedSuccessfully;

    // At startup, so the first unknown token costs one hash like every later one, not two.
    public static Task WarmAsync() => Dummy.Value;

    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewPassword() => RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);

    public static async Task<string> HashPasswordAsync(string password, CancellationToken ct)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = await DeriveAsync(password, salt, MemoryKib, Iterations, Parallelism, HashBytes, ct);
        return $"$argon2id$v=19$m={MemoryKib},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static async Task<bool> VerifyPasswordAsync(string password, string stored, CancellationToken ct)
    {
        var parts = stored.Split('$');
        var cost = parts[3].Split(',').Select(p => int.Parse(p[2..])).ToArray();
        var expected = Convert.FromBase64String(parts[5]);
        var actual = await DeriveAsync(password, Convert.FromBase64String(parts[4]), cost[0], cost[1], cost[2], expected.Length, ct);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // Spends the same work as a real check, for a token with no live link behind it.
    public static async Task VerifyAgainstDummyAsync(string password, CancellationToken ct) =>
        await VerifyPasswordAsync(password, await Dummy.Value, ct);

    private static async Task<byte[]> DeriveAsync(
        string password, byte[] salt, int memoryKib, int iterations, int parallelism, int length, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Empty or over-long input cannot be a generated password, and Argon2 refuses empty input: it still costs
        // one hash, of a byte outside the alphabet, so it answers like any wrong password.
        var bytes = password.Length is 0 or > MaxPasswordChars ? new byte[1] : Encoding.UTF8.GetBytes(password);
        using var argon2 = new Argon2id(bytes)
        {
            Salt = salt, MemorySize = memoryKib, Iterations = iterations, DegreeOfParallelism = parallelism,
        };
        return await argon2.GetBytesAsync(length);
    }
}

// How many Argon2 runs may be in flight across all clients: each costs 19 MiB and, for a live link, a database connection
// and a row lock. The slot is taken before any of that, so a burst waits in a short queue and the rest get 429 at once.
public sealed class ShareAttemptLimiter(int permits = ShareAttemptLimiter.DefaultPermits, int queue = ShareAttemptLimiter.DefaultQueue) : IDisposable
{
    public const int DefaultPermits = 4;
    public const int DefaultQueue = 8;

    private readonly ConcurrencyLimiter _limiter = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = permits, QueueLimit = queue, QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    public ValueTask<RateLimitLease> AcquireAsync(CancellationToken ct) => _limiter.AcquireAsync(1, ct);

    public void Dispose() => _limiter.Dispose();
}
