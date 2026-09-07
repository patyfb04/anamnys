using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Anamnys.Server.Auth;

public sealed class TokenRefresher(
    IHttpClientFactory httpClientFactory,
    ILogger<TokenRefresher> logger)
{
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    public async Task ValidateAsync(
        CookieValidatePrincipalContext context,
        string realm,
        string clientId,
        string clientSecret)
    {
        var expiresAtRaw = context.Properties.GetTokenValue("expires_at");

        // A missing or unparseable expires_at must not fail open: with
        // SlidingExpiration on, silently skipping the refresh would let the
        // cookie keep renewing indefinitely against a token we can no longer
        // vouch for. Treat it the same as "already expired" and let the
        // refresh call (and its own failure handling below) decide the
        // outcome, rather than trusting the session as-is.
        var needsRefresh = true;
        if (DateTimeOffset.TryParse(
                expiresAtRaw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt))
        {
            // Refresh a minute early so an in-flight request never races expiry.
            needsRefresh = expiresAt <= DateTimeOffset.UtcNow.AddMinutes(1);
        }

        if (!needsRefresh)
        {
            return;
        }

        var refreshToken = context.Properties.GetTokenValue("refresh_token");
        if (string.IsNullOrEmpty(refreshToken))
        {
            context.RejectPrincipal();
            return;
        }

        TokenResponse? tokens;
        try
        {
            var client = httpClientFactory.CreateClient("keycloak");
            using var response = await client.PostAsync(
                $"realms/{realm}/protocol/openid-connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                }),
                context.HttpContext.RequestAborted);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Refresh failed for realm {Realm} with status {Status}.", realm, response.StatusCode);
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name);
                return;
            }

            tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(context.HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Keycloak being unreachable or returning a non-JSON error body
            // must not propagate out of OnValidatePrincipal — that would turn
            // every authenticated request into a 500 for the duration of the
            // outage. Fail the same way an explicit refresh rejection does:
            // log the user out and let them re-authenticate.
            logger.LogWarning(ex, "Token refresh request failed for realm {Realm}.", realm);
            context.RejectPrincipal();
            return;
        }

        if (tokens is null)
        {
            context.RejectPrincipal();
            return;
        }

        context.Properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = tokens.AccessToken },
            new AuthenticationToken { Name = "refresh_token", Value = tokens.RefreshToken ?? refreshToken },
            new AuthenticationToken
            {
                Name = "expires_at",
                Value = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn).ToString("o"),
            },
        ]);

        context.ShouldRenew = true;
    }
}
