using System.Security.Claims;
using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();
builder.AddRedisClientBuilder("cache")
    .WithOutputCache();

builder.AddNpgsqlDbContext<AnamnysDbContext>("anamnysdb");
builder.Services.AddHostedService<DatabaseInitializer>();

builder.AddAnamnysAuthentication();

// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Fails closed outside Development if either dev-only Keycloak service-account
// client (anamnys-test-provider, anamnys-test-owner) still exists in its realm.
// Those clients carry a literal, publicly-known secret because the real BFF
// clients disable the password grant — fine for local development, and a PHI
// exposure everywhere else. Mirrors the fail-closed philosophy of the
// DataProtection certificate gate in AuthenticationSetup: if the check itself
// cannot run outside Development, that is also a startup failure, not a pass.
using (var gateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
{
    await DevOnlyTestClientGuard.EnsureNoDevOnlyTestClientsExistAsync(
        app.Environment.IsDevelopment(),
        app.Configuration["KEYCLOAK_ADMIN_USERNAME"],
        app.Configuration["KEYCLOAK_ADMIN_PASSWORD"],
        app.Configuration["KEYCLOAK_ADMIN_BASE_ADDRESS"],
        gateTimeout.Token);
}

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    var schemeProvider = app.Services.GetRequiredService<IAuthenticationSchemeProvider>();
    var registeredSchemes = (await schemeProvider.GetAllSchemesAsync())
        .Select(scheme => scheme.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToArray();
    app.Logger.LogInformation(
        "Registered authentication schemes ({Count}): {Schemes}",
        registeredSchemes.Length,
        string.Join(", ", registeredSchemes));
}

app.UseOutputCache();

// PHI lives behind provider and patient credentials only. The owners realm is
// absent from this list on purpose: an owner credential is not an
// authenticated principal here at all, so it fails with 401 rather than 403.
// See design/specs/2026-09-05-keycloak-implementation-design.md §2 and §4.
//
// Cookie schemes only. The three bearer schemes stay registered (see
// AuthenticationSetup) but are deliberately not mounted on any group yet:
// FirstLoginProvisioner runs on the OIDC handler, so AnamnysClaims.LocalId is
// minted on the cookie path alone, and CurrentUser.LocalId — the one
// sanctioned way to resolve a local row id, per spec §5 — throws for a bearer
// principal. Nothing calls these routes with a bearer token today, so mounting
// them buys nothing and guarantees a 500 for the first PHI endpoint that uses
// the documented identity path. When the spec §10.3 mobile client ships, add
// the bearer schemes back here *and* mint LocalId on JwtBearerEvents
// .OnTokenValidated in the same change.
var phi = app.MapGroup("/api/phi")
    .RequireAuthorization(policy => policy
        .AddAuthenticationSchemes(
            AuthSchemes.ProviderCookie,
            AuthSchemes.PatientCookie)
        .RequireAuthenticatedUser());

phi.MapGet("probe", (ClaimsPrincipal principal) => Results.Ok(new { localId = principal.LocalIdOrNull() }));

var admin = app.MapGroup("/api/admin")
    .RequireAuthorization(policy => policy
        .AddAuthenticationSchemes(AuthSchemes.OwnerCookie)
        .RequireAuthenticatedUser());

admin.MapGet("probe", (ClaimsPrincipal principal) => Results.Ok(new { localId = principal.LocalIdOrNull() }));

app.MapAuthEndpoints();

app.MapDefaultEndpoints();

app.UseFileServer();

app.Run();
