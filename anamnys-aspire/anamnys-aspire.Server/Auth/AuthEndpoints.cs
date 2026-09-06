using System.Security.Claims;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Auth;

public sealed record MeResponse(Guid Id, string Email, string Name, string Realm, string[] Roles);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        MapRealm(app, "provider", AuthSchemes.ProviderOidc, AuthSchemes.ProviderCookie, "/provider/");
        MapRealm(app, "patient", AuthSchemes.PatientOidc, AuthSchemes.PatientCookie, "/patient/");
        MapRealm(app, "owner", AuthSchemes.OwnerOidc, AuthSchemes.OwnerCookie, "/admin/");

        app.MapGet("/api/auth/me", async (
            HttpContext httpContext,
            AnamnysDbContext db,
            CancellationToken cancellationToken) =>
        {
            // Which realm this session belongs to has to come from which
            // cookie scheme actually authenticated the request, not from
            // principal.Identity.AuthenticationType: that value is whatever
            // the OIDC handshake stamped on the identity before it was ever
            // handed to the cookie's SignInAsync (typically the federation
            // authentication type), so it does not carry the cookie scheme
            // name and cannot be used to tell the three realms apart.
            var providerAuth = await httpContext.AuthenticateAsync(AuthSchemes.ProviderCookie);
            if (providerAuth.Succeeded)
            {
                return await LoadMe(providerAuth.Principal!, Realms.Providers, async localId =>
                {
                    var provider = await db.Providers.SingleOrDefaultAsync(p => p.Id == localId, cancellationToken);
                    return provider is null
                        ? null
                        : new MeResponse(provider.Id, provider.Email, provider.Name, Realms.Providers, []);
                });
            }

            var ownerAuth = await httpContext.AuthenticateAsync(AuthSchemes.OwnerCookie);
            if (ownerAuth.Succeeded)
            {
                return await LoadMe(ownerAuth.Principal!, Realms.Owners, async localId =>
                {
                    var staff = await db.Staff.SingleOrDefaultAsync(s => s.Id == localId, cancellationToken);
                    return staff is null
                        ? null
                        : new MeResponse(staff.Id, staff.Email, staff.Name, Realms.Owners, []);
                });
            }

            var patientAuth = await httpContext.AuthenticateAsync(AuthSchemes.PatientCookie);
            if (patientAuth.Succeeded)
            {
                return await LoadMe(patientAuth.Principal!, Realms.Patients, async localId =>
                {
                    var account = await db.PatientAccounts.SingleOrDefaultAsync(a => a.Id == localId, cancellationToken);
                    return account is null
                        ? null
                        : new MeResponse(account.Id, account.Email, account.Email, Realms.Patients, []);
                });
            }

            return Results.Unauthorized();

            async Task<IResult> LoadMe(ClaimsPrincipal principal, string realm, Func<Guid, Task<MeResponse?>> load)
            {
                var localId = principal.LocalIdOrNull();
                if (localId is null)
                {
                    return Results.Unauthorized();
                }

                var roles = principal.FindAll("roles").Select(c => c.Value).ToArray();
                var response = await load(localId.Value);
                return response is null
                    ? Results.Unauthorized()
                    : Results.Ok(response with { Roles = roles });
            }
        })
        .RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(AuthSchemes.ProviderCookie, AuthSchemes.PatientCookie, AuthSchemes.OwnerCookie)
            .RequireAuthenticatedUser());
    }

    private static void MapRealm(
        WebApplication app,
        string segment,
        string oidcScheme,
        string cookieScheme,
        string appPath)
    {
        // A full-page navigation, not an XHR: the response is a 302 to Keycloak
        // and the browser must follow it for the whole document.
        app.MapGet($"/auth/{segment}/login", (string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = SafeLocalRedirect(returnUrl, appPath) },
                [oidcScheme]))
            .AllowAnonymous();

        // Also a full-page navigation (a form POST from the SPA, not an XHR):
        // the OIDC leg of this sign-out answers with a 302 to Keycloak's
        // end_session_endpoint, which is cross-origin and carries no CORS
        // headers, so an XHR can only fail on it. When it fails, the __Host-
        // cookie is still cleared and the UI looks signed out while the
        // Keycloak SSO session survives — the next person to click "Log in"
        // on that browser is silently re-authenticated as the previous user.
        app.MapPost($"/auth/{segment}/logout", async (HttpContext httpContext) =>
        {
            // OpenIdConnectHandler.HandleSignOutAsync takes id_token_hint only
            // from the AuthenticationProperties handed to SignOut. Without it
            // Keycloak gets a logout request it cannot tie to a session and
            // (from 18 onwards) either prompts for confirmation or ignores it,
            // so the SSO session outlives the cookie. Read it here, before the
            // cookie leg runs: that leg removes the ticket from Redis, after
            // which the tokens are gone.
            var authenticateResult = await httpContext.AuthenticateAsync(cookieScheme);
            var idToken = authenticateResult.Properties?.GetTokenValue("id_token");

            var properties = new AuthenticationProperties { RedirectUri = appPath };
            if (!string.IsNullOrEmpty(idToken))
            {
                properties.StoreTokens([new AuthenticationToken { Name = "id_token", Value = idToken }]);
            }

            return Results.SignOut(properties, [cookieScheme, oidcScheme]);
        })
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(cookieScheme)
                .RequireAuthenticatedUser());
    }

    // Open-redirect guard: returnUrl comes from the query string of an
    // anonymous, unauthenticated GET, so it is fully attacker-controlled. Only
    // a path-and-query that stays on this origin is acceptable as a post-login
    // destination — anything else (an absolute URL, a scheme-relative "//evil"
    // URL, a "/\evil" browser-normalized variant) falls back to the realm's
    // default landing path instead of being handed to AuthenticationProperties
    // .RedirectUri, which performs no such validation itself.
    private static string SafeLocalRedirect(string? returnUrl, string fallback)
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return fallback;
        }

        // Embedded tab/CR/LF (e.g. "/\t/evil.com") is the residual class some
        // URL parsers strip back to "//evil.com" after the checks below have
        // already passed it — reject any control character outright before
        // looking at the rest of the shape.
        if (returnUrl.Any(char.IsControl))
        {
            return fallback;
        }

        if (returnUrl[0] != '/')
        {
            return fallback;
        }

        if (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))
        {
            return fallback;
        }

        return returnUrl;
    }
}
