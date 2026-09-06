using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
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
        builder.Services.AddScoped<FirstLoginProvisioner>();

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
        var dataProtectionBuilder = builder.Services.AddDataProtection()
            .SetApplicationName("anamnys");

        builder.Services.AddOptions<KeyManagementOptions>()
            .Configure<IConnectionMultiplexer>((options, redis) =>
            {
                options.XmlRepository = new RedisXmlRepository(
                    () => redis.GetDatabase(),
                    "anamnys:dataprotection-keys");
            });

        // Keys stored in Redis are readable by anything with read access to
        // Redis unless they are themselves encrypted at rest. Those keys
        // decrypt every AuthenticationTicket in RedisTicketStore, which holds
        // Keycloak access AND refresh tokens for all three PHI realms — so an
        // unencrypted key store turns a Redis exposure into a full PHI
        // credential compromise. In Development we accept the "No XML
        // encryptor configured" warning; everywhere else, missing key
        // protection is a startup failure, not a silent gap.
        var certificateThumbprint = builder.Configuration["DataProtection:CertificateThumbprint"];
        if (!string.IsNullOrEmpty(certificateThumbprint))
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var certificate = store.Certificates
                .Find(X509FindType.FindByThumbprint, certificateThumbprint, validOnly: false)
                .OfType<X509Certificate2>()
                .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"DataProtection:CertificateThumbprint '{certificateThumbprint}' was configured but no matching certificate was found in the CurrentUser/My store.");

            dataProtectionBuilder.ProtectKeysWithCertificate(certificate);
        }
        else if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "DataProtection:CertificateThumbprint is required outside Development. Without it, " +
                "DataProtection keys are persisted to Redis unencrypted, and those keys decrypt every " +
                "stored ticket's access and refresh tokens across all three PHI realms. Configure a " +
                "certificate (or an equivalent explicit key-protection mechanism) before starting the " +
                "app in this environment.");
        }

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

                // Stamps the local row id onto the principal before it ever
                // reaches the cookie. Every downstream provider-scoped query
                // reads CurrentUser.LocalId, never a client-supplied id, and
                // this is the one place that claim gets minted.
                options.Events.OnTokenValidated = async context =>
                {
                    var provisioner = context.HttpContext.RequestServices
                        .GetRequiredService<FirstLoginProvisioner>();

                    var localId = await provisioner.ProvisionAsync(
                        context.Principal!,
                        wiring.Realm,
                        context.HttpContext.RequestAborted);

                    var identity = (ClaimsIdentity)context.Principal!.Identity!;
                    identity.AddClaim(new Claim(AnamnysClaims.LocalId, localId.ToString()));
                };

                // An exception thrown above (an uninvited patient, an owners-
                // realm token with no recognised role, a disabled account) is
                // the most likely real failure, and without this it surfaces
                // as a bare 500 ProblemDetails after a fully successful
                // Keycloak login — incomprehensible to the user and no more
                // informative to us. RemoteFailure catches it before it gets
                // that far. The redirect carries no exception detail: only a
                // generic marker, never the message, which could otherwise
                // leak into browser history, Referer headers, or access logs.
                options.Events.OnRemoteFailure = context =>
                {
                    // Logged here because the redirect deliberately carries no
                    // detail: without this the loop a rejected provisioning
                    // used to produce was invisible on both ends. The
                    // exception itself is safe to log — FirstLoginProvisioner
                    // messages name a subject id at most, never a claim value.
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Anamnys.Server.Auth.RemoteFailure");
                    logger.LogWarning(
                        context.Failure,
                        "OIDC remote failure on scheme {Scheme} for realm {Realm}; redirecting to {Path} with authError.",
                        wiring.OidcScheme,
                        wiring.Realm,
                        wiring.SignedOutPath);

                    context.HandleResponse();
                    context.Response.Redirect($"{wiring.SignedOutPath}?authError=true");
                    return Task.CompletedTask;
                };

                // id_token_hint is what ties the logout request to the
                // Keycloak session; without it the __Host- cookie clears but
                // the SSO session survives and the next login on that browser
                // is silently re-authenticated as the previous user. The value
                // is put into these properties by AuthEndpoints' logout
                // endpoint, which reads it before the cookie leg of the
                // sign-out drops the ticket from Redis.
                options.Events.OnRedirectToIdentityProviderForSignOut = context =>
                {
                    var idToken = context.Properties.GetTokenValue("id_token");
                    if (!string.IsNullOrEmpty(idToken))
                    {
                        context.ProtocolMessage.IdTokenHint = idToken;
                    }

                    return Task.CompletedTask;
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
