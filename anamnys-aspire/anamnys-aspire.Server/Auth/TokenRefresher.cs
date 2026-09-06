using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Anamnys.Server.Auth;

public sealed class TokenRefresher(
    IHttpClientFactory httpClientFactory,
    ILogger<TokenRefresher> logger)
{
    private sealed record TokenResponse(
        string access_token,
        string? refresh_token,
        int expires_in);

    public async Task ValidateAsync(
        CookieValidatePrincipalContext context,
        string realm,
        string clientId,
        string clientSecret)
    {
        var expiresAtRaw = context.Properties.GetTokenValue("expires_at");
        if (!DateTimeOffset.TryParse(expiresAtRaw, out var expiresAt))
        {
            return;
        }

        // Refresh a minute early so an in-flight request never races expiry.
        if (expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return;
        }

        var refreshToken = context.Properties.GetTokenValue("refresh_token");
        if (string.IsNullOrEmpty(refreshToken))
        {
            context.RejectPrincipal();
            return;
        }

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

        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(context.HttpContext.RequestAborted);
        if (tokens is null)
        {
            context.RejectPrincipal();
            return;
        }

        context.Properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = tokens.access_token },
            new AuthenticationToken { Name = "refresh_token", Value = tokens.refresh_token ?? refreshToken },
            new AuthenticationToken
            {
                Name = "expires_at",
                Value = DateTimeOffset.UtcNow.AddSeconds(tokens.expires_in).ToString("o"),
            },
        ]);

        context.ShouldRenew = true;
    }
}
