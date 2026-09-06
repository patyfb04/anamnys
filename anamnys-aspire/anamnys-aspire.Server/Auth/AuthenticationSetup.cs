using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace Anamnys.Server.Auth;

public static class AuthenticationSetup
{
    private sealed record RealmWiring(
        string Realm,
        string CookieScheme,
        string OidcScheme,
        string BearerScheme,
        string CallbackPath,
        string SignedOutPath,
        string ClientId,
        string ClientSecretConfigKey);

    private static readonly RealmWiring[] Wirings =
    [
        new(Realms.Providers, AuthSchemes.ProviderCookie, AuthSchemes.ProviderOidc, AuthSchemes.ProviderBearer,
            "/signin-oidc-provider", "/provider/", "anamnys-web", "ANAMNYS_PROVIDER_CLIENT_SECRET"),
        new(Realms.Patients, AuthSchemes.PatientCookie, AuthSchemes.PatientOidc, AuthSchemes.PatientBearer,
            "/signin-oidc-patient", "/patient/", "anamnys-patient-web", "ANAMNYS_PATIENT_CLIENT_SECRET"),
        new(Realms.Owners, AuthSchemes.OwnerCookie, AuthSchemes.OwnerOidc, AuthSchemes.OwnerBearer,
            "/signin-oidc-owner", "/admin/", "anamnys-admin-web", "ANAMNYS_OWNER_CLIENT_SECRET"),
    ];

    public static IHostApplicationBuilder AddAnamnysAuthentication(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<TokenRefresher>();
        builder.Services.AddSingleton<ITicketStore, RedisTicketStore>();

        // Shared DataProtection keys, so ticket-store payloads and cookies stay
        // readable across restarts and replicas.
        //
        // The XmlRepository is wired through the options pattern rather than
        // via PersistKeysToStackExchangeRedis(IConnectionMultiplexer, ...): that
        // overload needs the multiplexer instance immediately, which is why the
        // brief reached for builder.Services.BuildServiceProvider() (builds a
        // second, throwaway container — can create a duplicate connection).
        // AddOptions<KeyManagementOptions>().Configure<IConnectionMultiplexer>
        // instead resolves IConnectionMultiplexer lazily from the real
        // container the app actually runs on — the same singleton Aspire's
        // AddRedisClientBuilder("cache") registers — with no extra connection
        // and no intermediate provider.
        builder.Services.AddDataProtection()
            .SetApplicationName("anamnys");

        builder.Services.AddOptions<KeyManagementOptions>()
            .Configure<IConnectionMultiplexer>((options, redis) =>
            {
                options.XmlRepository = new RedisXmlRepository(
                    () => redis.GetDatabase(),
                    "anamnys:dataprotection-keys");
            });

        var authentication = builder.Services.AddAuthentication();

        foreach (var wiring in Wirings)
        {
            var clientSecret = builder.Configuration[wiring.ClientSecretConfigKey]
                ?? throw new InvalidOperationException($"Missing configuration {wiring.ClientSecretConfigKey}.");

            authentication.AddCookie(wiring.CookieScheme, options =>
            {
                // __Host- forces Secure, forbids Domain and requires Path=/.
                // Path-scoping to /provider/ would look tidier and would break
                // everything: the cookie would not be sent to /api/*.
                options.Cookie.Name = wiring.CookieScheme switch
                {
                    AuthSchemes.ProviderCookie => "__Host-anamnys-provider",
                    AuthSchemes.PatientCookie => "__Host-anamnys-patient",
                    _ => "__Host-anamnys-owner",
                };
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.Path = "/";
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(10);

                // API clients get a status code, never a redirect to a login page.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnValidatePrincipal = async context =>
                {
                    var refresher = context.HttpContext.RequestServices.GetRequiredService<TokenRefresher>();
                    await refresher.ValidateAsync(context, wiring.Realm, wiring.ClientId, clientSecret);
                };
            });

            authentication.AddKeycloakOpenIdConnect("keycloak", wiring.Realm, wiring.OidcScheme, options =>
            {
                options.SignInScheme = wiring.CookieScheme;
                options.ClientId = wiring.ClientId;
                options.ClientSecret = clientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                // Tokens are handed to the cookie's AuthenticationProperties here,
                // but the cookie itself never carries them: the ITicketStore
                // wired below serializes the whole ticket (properties included)
                // server-side in Redis and leaves only an opaque key in the
                // browser's cookie.
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.CallbackPath = wiring.CallbackPath;
                options.SignedOutCallbackPath = wiring.CallbackPath + "-signout";
                options.SignedOutRedirectUri = wiring.SignedOutPath;
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles",
                    ValidateIssuer = true,
                    ValidateAudience = true,
                };
            });

            authentication.AddKeycloakJwtBearer("keycloak", wiring.Realm, wiring.BearerScheme, options =>
            {
                options.Audience = "anamnys-api";
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles",
                    ValidateIssuer = true,
                    ValidateAudience = true,
                };
            });
        }

        builder.Services.AddAuthorization();

        // Wire the shared ticket store into each cookie scheme after the fact,
        // so the store can take its own dependencies from DI.
        foreach (var wiring in Wirings)
        {
            builder.Services.AddOptions<CookieAuthenticationOptions>(wiring.CookieScheme)
                .Configure<ITicketStore>((options, store) => options.SessionStore = store);
        }

        // https+http:// (not a hardcoded http://) so Aspire service discovery
        // resolves the scheme Keycloak is actually serving in this
        // environment — https on port 8080. ConfigureHttpClientDefaults in
        // Extensions.cs applies AddServiceDiscovery() to every HttpClient
        // registered through the factory, this named client included.
        builder.Services.AddHttpClient("keycloak", client =>
        {
            client.BaseAddress = new Uri("https+http://keycloak");
        });

        return builder;
    }
}
