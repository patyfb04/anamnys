using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Anamnys.Server.Sharing;

// Who the client is, for the rate limits and for the startup checks around the public share link.
public static class ShareNetwork
{
    private const int Ipv6PrefixBytes = 8;

    // One IPv6 customer usually holds a whole /64: a limit per address would be a limit per guess.
    public static string PartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"{Convert.ToHexStringLower(address.GetAddressBytes().AsSpan(0, Ipv6PrefixBytes))}/64"
            : address.ToString();
    }

    // ForwardedHeaders:KnownProxies lists the reverse proxies allowed to set X-Forwarded-For. Empty, the default, means
    // the header is ignored and the limits see the connection's own address. A value that is not an address stops startup.
    public static IPAddress[] KnownProxies(IConfiguration configuration) =>
        (configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
            .Select(value => IPAddress.TryParse(value, out var address)
                ? address
                : throw new InvalidOperationException($"ForwardedHeaders:KnownProxies has an invalid address: {value}."))
            .ToArray();

    // Mounted only with known proxies. Only the client address: scheme and host stay as they are, because the cookies and
    // the sign-in redirects depend on them.
    public static void UseForwardedClientAddress(this WebApplication app)
    {
        var proxies = KnownProxies(app.Configuration);
        if (proxies.Length == 0)
        {
            return;
        }
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in proxies)
        {
            options.KnownProxies.Add(proxy);
        }
        app.UseForwardedHeaders(options);
    }

    // The link handed to a provider must not be built from a request's Host header. Same shape as Storage:PublicUrl.
    public static void RequirePortalUrl(bool isDevelopment, string? baseUrl)
    {
        if (!isDevelopment && !(Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("PatientPortal:BaseUrl must be an absolute https URL outside Development.");
        }
    }
}
