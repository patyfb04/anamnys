# Chapter 4: .NET Aspire Orchestration (the AppHost)

## What the AppHost actually is

`anamnys-aspire.AppHost` is a C# executable, but it doesn't run application logic — it
runs *once*, builds a graph describing every resource the system needs and how they
depend on each other, and then either starts that graph locally (`aspire run`/`aspire
start`) or emits it as deployment artifacts (`aspire publish`/`aspire deploy`). Nothing in
`AppHost.cs` ships to production as a running process; it's closer to infrastructure-as-
code written in C# than to a service.

The whole file is `anamnys-aspire.AppHost/AppHost.cs`, and it's short enough — about 120
lines — that reading it end to end genuinely does teach you the shape of the whole
system. This chapter walks it top to bottom.

## Building the resource graph

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");
```

Every resource starts life as an `Add*` call on `builder`, and each call returns a handle
you can keep chaining configuration onto or hand to later resources as a dependency.
`cache` here is Redis — used for two unrelated things later in the system: ASP.NET Core
output caching, and the server-side session ticket store described in Chapter 8.

### The secret parameters

```csharp
var providerClientSecret = builder.AddParameter("provider-client-secret", secret: true);
var patientClientSecret = builder.AddParameter("patient-client-secret", secret: true);
var ownerClientSecret = builder.AddParameter("owner-client-secret", secret: true);

var keycloakAdminUsername = builder.AddParameter("keycloak-admin-username", "admin");
var keycloakAdminPassword = builder.AddParameter("keycloak-admin-password", secret: true);
```

These are the same four values you set with `dotnet user-secrets` in Chapter 3 — this is
where the AppHost declares that it *needs* them. `secret: true` with no default means
Aspire refuses to start without a real value; `keycloakAdminUsername` is the one exception,
defaulting to `"admin"` right here rather than needing a secret at all. The comment in the
source is worth repeating, because it explains a decision that looks unnecessary at first
glance: these are set explicitly rather than left to `AddKeycloak`'s own bootstrap
defaults, specifically so that the server's startup gate (`DevOnlyTestClientGuard`,
Chapter 8) has a stable, known admin credential to query Keycloak's admin API with,
outside Development.

### Postgres, with two logical databases

```csharp
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();

var keycloakDb = postgres.AddDatabase("keycloakdb");
var anamnysDb = postgres.AddDatabase("anamnysdb");
```

One Postgres *container*, two logical *databases* inside it. Keycloak gets its own
(`keycloakdb`) — which, as you'll see in Chapter 7, is actually where Keycloak's realm
data physically lives, not in a Keycloak-owned volume. The application gets its own
(`anamnysdb`), which is what `AnamnysDbContext` (Chapter 9) connects to. `WithDataVolume()`
is what makes this data survive an `aspire stop`/`aspire start` cycle — and, as covered in
Chapters 3 and 7, exactly what has to be removed to force a fresh Keycloak realm import.

### The server, declared early on purpose

```csharp
var server = builder.AddProject<Projects.anamnys_aspire_Server>("server")
    .WithReference(cache)
    .WithReference(anamnysDb)
    .WaitFor(cache)
    .WaitFor(anamnysDb)
    .WithEnvironment("ANAMNYS_PROVIDER_CLIENT_SECRET", providerClientSecret)
    .WithEnvironment("ANAMNYS_PATIENT_CLIENT_SECRET", patientClientSecret)
    .WithEnvironment("ANAMNYS_OWNER_CLIENT_SECRET", ownerClientSecret)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();
```

This is `AddProject<T>` — Aspire's way of adding a *.NET* project to the graph, as
opposed to `AddViteApp` for a JavaScript one (you'll see that below) or `AddPostgres`/
`AddRedis`/`AddKeycloak` for a container-backed resource. `WithReference` +
`WaitFor` together do two things: they inject connection information for `cache` and
`anamnysDb` into the server's configuration (this is what lets `Program.cs` just say
`builder.AddNpgsqlDbContext<AnamnysDbContext>("anamnysdb")` without a literal connection
string anywhere — see Chapter 5), and they make sure those two resources are healthy
*before* the server starts.

Why is `server` declared here, *before* `keycloak` even exists yet? The comment in the
source spells out the dependency puzzle directly: Keycloak needs to know the server's own
origin (`ANAMNYS_APP_ORIGIN`, so it can validate redirect URIs against the right host), so
`keycloak` has to depend on `server`'s endpoint. If `server` also tried to `WaitFor
(keycloak)` at the point it's declared, that would be a circular wait — two resources each
blocking on the other. The fix is ordering: declare `server` (without yet waiting on
Keycloak), declare `keycloak` referencing `server`'s endpoint, and only *then*, once both
exist, add the `server.WithReference(keycloak).WaitFor(keycloak)` call a few lines further
down. The graph ends up correct; it just can't be written as one unbroken chain.

### Keycloak: a custom-built container, not the stock image

```csharp
var keycloak = builder.AddKeycloak("keycloak", 8080, keycloakAdminUsername, keycloakAdminPassword)
    .WithDockerfile("../keycloak")
    .WithBuildArg(
        "INCLUDE_DEV_SEED",
        builder.ExecutionContext.IsRunMode && builder.Environment.IsDevelopment())
    .WithPostgres(keycloakDb)
    .WithDataVolume()
    .WithOtlpExporter()
    .WithEnvironment("ANAMNYS_PROVIDER_CLIENT_SECRET", providerClientSecret)
    .WithEnvironment("ANAMNYS_PATIENT_CLIENT_SECRET", patientClientSecret)
    .WithEnvironment("ANAMNYS_OWNER_CLIENT_SECRET", ownerClientSecret)
    .WithEnvironment("ANAMNYS_APP_ORIGIN", server.GetEndpoint("http"))
    .WaitFor(keycloakDb);

server.WithReference(keycloak).WaitFor(keycloak);
```

`.WithDockerfile("../keycloak")` is the detail that separates this from a typical Aspire
Keycloak resource: instead of pulling the stock Keycloak image, this builds a custom one
from `keycloak/Dockerfile` (Chapter 7 covers that file in full — it's where the realm
JSON gets baked in and, conditionally, stripped of dev-only seed data).

The `INCLUDE_DEV_SEED` build arg is worth slowing down on, because the logic behind it is
one of the more subtle pieces of reasoning in this file, and getting it wrong would be a
real security bug, not a cosmetic one. The naive version — "just check
`Environment.IsDevelopment()`" — is wrong, because `Properties/launchSettings.json` pins
`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` to `Development` in *every* launch profile.
That means on a developer's own machine, `IsDevelopment()` is `true` no matter what
command they're actually running — including `aspire publish`, which is supposed to
produce a deployable artifact. If the gate were `IsDevelopment()` alone, `aspire publish`
run from a laptop would happily bake dev-only Keycloak seed credentials into an image
meant for a real environment. `builder.ExecutionContext.IsRunMode` is the piece that
fixes this: it's `false` for `aspire publish`/`aspire deploy` regardless of what
environment variable is set, and `true` only for an actual `aspire run`/`aspire start`.
Only when *both* conditions hold — really running locally, in a Development environment —
does the dev seed get included. This is exactly the kind of comment worth reading in
place rather than trusting a summary of; if you ever touch this line, read the reasoning
in the file again first.

`WithPostgres(keycloakDb)` and `WithDataVolume()` connect Keycloak to its logical database
and give it a persistent volume — see the note above and Chapter 7 for what "persistent"
means when you edit realm JSON. The three `ANAMNYS_*_CLIENT_SECRET` environment variables
flow into Keycloak's realm import (they get substituted into the realm JSON as
`${ANAMNYS_*_CLIENT_SECRET}` placeholders), and `ANAMNYS_APP_ORIGIN` is the server's own
endpoint, resolved via `server.GetEndpoint("http")` — this is the piece of the dependency
puzzle described above finally being wired up. Only after `keycloak` exists does the file
go back and add `server.WithReference(keycloak).WaitFor(keycloak)`, completing the graph
in both directions without ever forming a cycle.

### The admin API wiring for the startup gate

```csharp
server
    .WithEnvironment("KEYCLOAK_ADMIN_USERNAME", keycloakAdminUsername)
    .WithEnvironment("KEYCLOAK_ADMIN_PASSWORD", keycloakAdminPassword)
    .WithEnvironment("KEYCLOAK_ADMIN_BASE_ADDRESS", keycloak.GetEndpoint("http"));
```

This is purely in service of `DevOnlyTestClientGuard` (Chapter 8), which needs to call
Keycloak's admin API on startup to check whether dev-only test clients still exist.
`KEYCLOAK_ADMIN_BASE_ADDRESS` is deliberately a **plain resolved endpoint**, not the
`https+http://keycloak` service-discovery pseudo-scheme used everywhere else in this file
— that scheme only resolves under Aspire's own orchestration, and this particular gate has
to keep working (or fail safely) even outside it, in a real deployment that isn't run
through Aspire at all.

### The four SPAs

```csharp
var web = builder.AddViteApp("web", "../apps/web")
    .WithReference(server)
    .WaitFor(server)
    .WithHttpEndpoint(port: 5275, targetPort: 5275, isProxied: false);

var provider = builder.AddViteApp("provider", "../apps/provider")
    .WithReference(server)
    .WaitFor(server)
    .WithHttpEndpoint(port: 5273, targetPort: 5273, isProxied: false);

var patient = builder.AddViteApp("patient", "../apps/patient")
    .WithReference(server)
    .WaitFor(server)
    .WithHttpEndpoint(port: 5274, targetPort: 5274, isProxied: false);

var admin = builder.AddViteApp("admin", "../apps/admin")
    .WithReference(server)
    .WaitFor(server)
    .WithHttpEndpoint(port: 5276, targetPort: 5276, isProxied: false);
```

`AddViteApp` is the bridge between the .NET solution and the npm workspace described in
Chapter 2 — this is the line that lets the AppHost start, monitor, and later publish a
JavaScript SPA the same way it treats any other resource. Each one waits for `server`
because their dev-time Vite proxy needs the API to be reachable.

The ports (5273–5276) are pinned rather than left to Aspire's usual dynamic assignment,
and this is not a style preference — it's load-bearing for authentication. The server
constructs its OIDC `redirect_uri` from the `Host` header of whatever origin the browser
is actually using, which — in dev — is whichever port that app's Vite dev server is
running on. That origin has to stay the *same* origin across AppHost restarts, because
it's also registered as an allowed redirect URI inside each realm's Keycloak client
configuration (Chapter 7). A port that moved between restarts would silently break login
with a redirect-URI mismatch, and the failure would look like a Keycloak configuration
problem rather than an Aspire one.

### From four dev resources to one production container

```csharp
server.PublishWithContainerFiles(web, "wwwroot");
server.PublishWithContainerFiles(provider, "wwwroot/provider");
server.PublishWithContainerFiles(patient, "wwwroot/patient");
server.PublishWithContainerFiles(admin, "wwwroot/admin");

builder.Build().Run();
```

This is where the dev-time and production-time pictures diverge most sharply. In
development, you have five separate running processes: the server and four independent
Vite dev servers, each on its own pinned port, proxying to the server for `/api`. In
production, `PublishWithContainerFiles` bakes each app's *built* static assets directly
into a sub-path of the server's own `wwwroot` at publish time — `web` at the root,
`provider` at `/provider`, `patient` at `/patient`, `admin` at `/admin` — so that what
ships is a **single container**, serving four SPAs and an API, all same-origin with each
other. Chapter 5 picks this up from the server side with `UseFileServer()`, and Chapter 11
explains in more depth why same-origin specifically matters (in one word: cookies).

## The pattern to take away

Nearly everything unusual in this file traces back to one constraint: **origins and
identity have to agree with each other everywhere, all the time.** The pinned ports, the
`ANAMNYS_APP_ORIGIN` wiring, the redirect-URI coupling, even the `IsRunMode` vs.
`IsDevelopment()` distinction — all of it exists because a BFF cookie-based auth system
(Chapter 6) is fundamentally about trusting a specific origin, and this file is the place
where "what origin is this, really, right now" gets decided for every environment the
system runs in.

Chapter 5 moves inside the `server` resource itself, to see what happens once a request
actually reaches it.
