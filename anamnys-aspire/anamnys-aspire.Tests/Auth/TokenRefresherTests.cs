using System.Net;
using System.Security.Claims;
using System.Text;
using Anamnys.Server.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Anamnys.Tests.Auth;

// Keycloak only accepts a refresh token at the address that issued it: a token minted
// through the OIDC handler's authority (https://localhost:8443) and refreshed through any
// other address (service discovery resolved https://localhost:8080) fails with "Invalid
// token issuer", and every session died when its first access token expired. The
// refresher must therefore use the OIDC handler's own discovered token endpoint and
// backchannel, never a separately configured Keycloak address.
public class TokenRefresherTests
{
    private const string OidcScheme = "provider-oidc";
    private const string TokenEndpoint = "https://issuer.test:8443/realms/anamnys-providers/protocol/openid-connect/token";

    [Fact]
    public async Task ValidateAsync_WhenTokenExpiring_RefreshesAtTheOidcHandlersTokenEndpoint()
    {
        // Arrange
        var keycloak = new CapturingHandler("""{"access_token":"a2","refresh_token":"r2","expires_in":300}""");
        var refresher = new TokenRefresher(OidcOptions(keycloak), NullLogger<TokenRefresher>.Instance);
        var context = ExpiringSession("r1");

        // Act
        await refresher.ValidateAsync(context, OidcScheme, "anamnys-providers", "anamnys-web", "secret");

        // Assert
        keycloak.RequestUri.Should().Be(new Uri(TokenEndpoint));
        keycloak.Body.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=r1");
        context.ShouldRenew.Should().BeTrue();
        context.Properties.GetTokenValue("access_token").Should().Be("a2");
        context.Properties.GetTokenValue("refresh_token").Should().Be("r2");
        context.Principal.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_WhenTokenStillFresh_DoesNotCallKeycloak()
    {
        // Arrange
        var keycloak = new CapturingHandler("{}");
        var refresher = new TokenRefresher(OidcOptions(keycloak), NullLogger<TokenRefresher>.Instance);
        var context = ExpiringSession("r1", expiresAt: DateTimeOffset.UtcNow.AddMinutes(4));

        // Act
        await refresher.ValidateAsync(context, OidcScheme, "anamnys-providers", "anamnys-web", "secret");

        // Assert
        keycloak.RequestUri.Should().BeNull();
        context.ShouldRenew.Should().BeFalse();
    }

    private static IOptionsMonitor<OpenIdConnectOptions> OidcOptions(HttpMessageHandler backchannel)
    {
        var options = new OpenIdConnectOptions
        {
            Backchannel = new HttpClient(backchannel),
            ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                new OpenIdConnectConfiguration { TokenEndpoint = TokenEndpoint }),
        };
        return new NamedOptions(OidcScheme, options);
    }

    private static CookieValidatePrincipalContext ExpiringSession(string refreshToken, DateTimeOffset? expiresAt = null)
    {
        var properties = new AuthenticationProperties();
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = "a1" },
            new AuthenticationToken { Name = "refresh_token", Value = refreshToken },
            new AuthenticationToken { Name = "expires_at", Value = (expiresAt ?? DateTimeOffset.UtcNow.AddSeconds(10)).ToString("o") },
        ]);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "s")], "test"));
        var scheme = new AuthenticationScheme("provider-cookie", null, typeof(CookieAuthenticationHandler));
        return new CookieValidatePrincipalContext(
            new DefaultHttpContext(), scheme, new CookieAuthenticationOptions(), new AuthenticationTicket(principal, properties, scheme.Name));
    }

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class NamedOptions(string name, OpenIdConnectOptions options) : IOptionsMonitor<OpenIdConnectOptions>
    {
        public OpenIdConnectOptions CurrentValue => options;
        public OpenIdConnectOptions Get(string? requested) =>
            requested == name ? options : throw new InvalidOperationException($"No options for {requested}.");
        public IDisposable? OnChange(Action<OpenIdConnectOptions, string?> listener) => null;
    }
}
