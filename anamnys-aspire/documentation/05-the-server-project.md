# Chapter 5: The Server Project

`anamnys-aspire.Server` is the single .NET minimal API that backs every client in this
system — all four SPAs, and eventually a mobile client (see the forward-looking comments
in Chapter 8). This chapter covers the two files that shape everything the server does
before you reach any specific feature: `Extensions.cs`, the reusable "service defaults"
every Aspire-orchestrated project starts from, and `Program.cs`, the server's actual
composition root.

## `Extensions.cs`: the service defaults pattern

If you've worked with an Aspire template before, `Extensions.cs` will look familiar — it's
the standard "service defaults" file the `dotnet new aspire` templates generate, and
Anamnys hasn't deviated from it much. It exists as a separate static class specifically so
that *any* project added to the solution later (a second API, a background worker) could
call the same `AddServiceDefaults()` and get identical baseline behavior, even though
today only the server project uses it.

```csharp
public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
    where TBuilder : IHostApplicationBuilder
{
    builder.ConfigureOpenTelemetry();
    builder.AddDefaultHealthChecks();
    builder.Services.AddServiceDiscovery();

    builder.Services.ConfigureHttpClientDefaults(http =>
    {
        http.AddStandardResilienceHandler();
        http.AddServiceDiscovery();
    });

    return builder;
}
```

Three things happen here that are easy to take for granted but worth naming explicitly:

- **OpenTelemetry** is wired for every project that calls this — traces, metrics, and
  logs all flow through the OpenTelemetry pipeline, exported via OTLP when
  `OTEL_EXPORTER_OTLP_ENDPOINT` is configured (which the AppHost sets automatically when
  running under `aspire run`, via the dashboard). `ConfigureOpenTelemetry` specifically
  excludes `/health` and `/alive` from tracing — otherwise the dashboard's own health
  polling would flood the trace view with noise.
- **Every `HttpClient` created through the factory gets resilience and service discovery
  by default** — `AddStandardResilienceHandler()` (retries, timeouts, circuit breaking)
  and `AddServiceDiscovery()` (so a client can be pointed at `https+http://keycloak`
  rather than a literal URL and have Aspire resolve it). This is *why* the `"keycloak"`
  named `HttpClient` registered in `AuthenticationSetup.cs` (Chapter 8) doesn't need any
  resilience configuration of its own — it inherits these defaults automatically, just by
  going through `IHttpClientFactory`.
- **Health checks** are split into two endpoints deliberately: `/health` (all checks must
  pass — used as the readiness signal, and it's exactly what `AppHost.cs`'s
  `WithHttpHealthCheck("/health")` polls) and `/alive` (only checks tagged `"live"` — a
  narrower liveness signal). Both are only mapped when `IsDevelopment()` is true; the
  comment in the source is explicit about why they aren't exposed unconditionally —
  exposing health check details in non-development environments has real security
  implications, so it's opt-in per environment rather than a default to override later.

## `Program.cs`: the composition root

### Building up the app

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClientBuilder("cache")
    .WithOutputCache();

builder.AddNpgsqlDbContext<AnamnysDbContext>("anamnysdb");
builder.Services.AddHostedService<DatabaseInitializer>();

builder.AddAnamnysAuthentication();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
```

Read top to bottom, this is genuinely a good map of everything the server depends on:
service defaults (Chapter above), a Redis client used specifically for output caching
(distinct from the *other* use of the same Redis instance — the auth ticket store, wired
up inside `AddAnamnysAuthentication()`, Chapter 8), the EF Core `DbContext` pointed at the
`"anamnysdb"` resource declared in the AppHost (no connection string appears here at all —
Aspire's service discovery resolves it), a hosted service that bootstraps the database
schema on startup (`DatabaseInitializer`, covered in Chapter 9), the entire authentication
system as one extension method (Chapter 8 is dedicated to what that call does), and
standard ASP.NET Core plumbing for RFC 7807 problem details and OpenAPI document
generation.

### The startup gate that runs before anything else

```csharp
using (var gateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
{
    await DevOnlyTestClientGuard.EnsureNoDevOnlyTestClientsExistAsync(
        app.Environment.IsDevelopment(),
        app.Configuration["KEYCLOAK_ADMIN_USERNAME"],
        app.Configuration["KEYCLOAK_ADMIN_PASSWORD"],
        app.Configuration["KEYCLOAK_ADMIN_BASE_ADDRESS"],
        gateTimeout.Token);
}
```

This runs *before* the request pipeline is configured at all, and it's a **fail-closed**
check: outside Development, if either of the dev-only Keycloak service-account clients
(`anamnys-test-provider`, `anamnys-test-owner`) still exists in its realm, the server
refuses to start. Those two clients exist purely to make local/test login flows scriptable
— they carry a literal, publicly-known secret, because the real BFF clients disable the
password grant entirely (Chapter 6 explains why the password grant is off for the real
clients). That's harmless in local development and a genuine PHI exposure anywhere else.
This mirrors a philosophy you'll see again with the DataProtection certificate check in
Chapter 8: if a security check *itself* can't run (say, the admin API is unreachable),
that's treated as a startup failure too, not as an implicit pass. Chapter 8 covers what
`DevOnlyTestClientGuard` actually does in detail; the point to take from `Program.cs` is
*where* it runs — as early as possible, blocking everything after it.

### The request pipeline

```csharp
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // ...logs every registered authentication scheme...
}

app.UseOutputCache();
```

Standard middleware ordering, with one dev-only convenience worth knowing about: in
Development, the server logs every registered authentication scheme's name at startup.
Given how many schemes this system registers per realm — a cookie scheme, an OIDC scheme,
and a bearer scheme, times three realms, per Chapter 8 — this log line is a genuinely
useful sanity check when you're debugging why a particular scheme isn't behaving as
expected. It costs nothing in production because it's gated behind the same
`IsDevelopment()` check as the OpenAPI endpoint.

### The two PHI-adjacent endpoint groups

```csharp
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
```

This is the pattern every future endpoint in this system is expected to follow, and it's
worth understanding precisely, because the design here is easy to get subtly wrong if you
reach for the more familiar `[Authorize(Roles = "...")]` mental model instead.

Each group's `.AddAuthenticationSchemes(...)` call is an *allow-list of realms*, not a
role check. `/api/phi` accepts the provider cookie scheme and the patient cookie scheme —
notice the owners realm is conspicuously **absent** from that list. This is deliberate,
and the practical effect is important: a request carrying a perfectly valid owner-realm
session cookie, hitting `/api/phi/*`, doesn't get a 403 Forbidden. It gets a **401
Unauthorized**, because as far as that endpoint group is concerned, an owner credential
isn't an authenticated principal *at all* — the scheme it authenticated against was never
registered on this group in the first place. If you ever see a PHI endpoint return 403
instead of 401 for a wrong-realm credential, that's a sign a scheme has leaked into a
group it shouldn't be in, and the fix belongs in the group's scheme list, never in a test
that's asserting the "wrong" status code.

Every future `/api/phi/*` or `/api/admin/*` endpoint is meant to hang off `phi` or `admin`
(via `phi.MapGet(...)`, `phi.MapPost(...)`, and so on) rather than defining its own
authorization policy from scratch — see the endpoint-creation workflow in Chapter 18.

### Serving the SPAs

```csharp
app.MapAuthEndpoints();
app.MapDefaultEndpoints();
app.UseFileServer();
app.Run();
```

`MapAuthEndpoints()` wires up the login/logout/`/api/auth/me` routes covered in depth in
Chapter 8. `MapDefaultEndpoints()` (back in `Extensions.cs`) maps the `/health` and
`/alive` checks discussed above. `UseFileServer()` is the server-side half of Chapter 4's
`PublishWithContainerFiles` calls — it's what actually serves the static files that end up
in `wwwroot`, `wwwroot/provider`, `wwwroot/patient`, and `wwwroot/admin` once the app is
published, making good on the "one container, four apps, same origin" architecture
described there.

## What's *not* here yet

It's worth being explicit, per Chapter 1's advice to always know what's built versus
planned: as of this writing, there is no request-validation library wired in (Chapter 1's
"planned" list names FluentValidation), no OpenAPI documentation UI beyond the raw
document (Scalar is planned), and — as Chapter 16 covers — the test project doesn't yet
use xUnit + FluentAssertions in the conventional sense the CLAUDE.md "planned" section
describes. None of this is a gap in the code you're reading; it's simply how far the
project has gotten. The shape of `Program.cs` above — service defaults, a DbContext, a
startup gate, two authorization-scoped endpoint groups, and a file server — is the
skeleton every future PHI-touching feature (transcription, note structuring, billing) will
be grafted onto, following the pattern this chapter just walked through.

With the platform itself covered, Part 4 goes deep on the piece of this server that has
had, by far, the most engineering attention: identity.
