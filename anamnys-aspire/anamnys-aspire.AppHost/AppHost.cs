using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

var providerClientSecret = builder.AddParameter("provider-client-secret", secret: true);
var patientClientSecret = builder.AddParameter("patient-client-secret", secret: true);
var ownerClientSecret = builder.AddParameter("owner-client-secret", secret: true);

// Explicit, rather than left to AddKeycloak's own bootstrap defaults, so the
// server's outside-Development startup gate (DevOnlyTestClientGuard) has a
// stable, resolvable admin credential to query Keycloak's admin API with.
var keycloakAdminUsername = builder.AddParameter("keycloak-admin-username", "admin");
var keycloakAdminPassword = builder.AddParameter("keycloak-admin-password", secret: true);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();

var keycloakDb = postgres.AddDatabase("keycloakdb");
var anamnysDb = postgres.AddDatabase("anamnysdb");

// Same discriminator as INCLUDE_DEV_SEED below — see that comment for why both
// conditions are needed. Named here because it now gates two independent things
// (the realm's dev-only content, and the Mailpit resource just below).
var isDevRun = builder.ExecutionContext.IsRunMode && builder.Environment.IsDevelopment();

// Keycloak's verifyEmail registration/first-login step needs somewhere to actually
// send mail — no environment has a real SMTP relay wired up yet (production still
// needs one; see keycloak/dev-only-overrides.jq), and without it Keycloak's "send
// verification email" step always fails, dead-ending every fresh registration.
//
// Two options, decided per developer: Mailpit (default, see below) or a real Resend
// account. Read directly (not via AddParameter) so Resend is genuinely optional:
// AddParameter's secret:true with no default forces every developer to configure it,
// which is wrong for something this personal and optional — set your own with
// `dotnet user-secrets set "Parameters:resend-api-key" "<key>"` from this AppHost
// project directory, and dev-only-overrides.jq's SMTP block points at Resend's relay
// instead of Mailpit; actual email lands in your own inbox rather than Mailpit's
// catcher. Nobody else's setup changes whether or not this is set.
//
// Not wired up for anything outside a dev run — Resend is a third-party mail
// provider Anamnys has no BAA with, so it must never carry real user data; this
// path exists only for one developer's own local testing with their own key.
// Production's own SMTP relay is a separate, not-yet-built piece of work — see
// dev-only-overrides.jq.
var resendApiKey = builder.Configuration["Parameters:resend-api-key"];
var useResend = isDevRun && !string.IsNullOrEmpty(resendApiKey);

// Mailpit is a throwaway SMTP catcher + web UI (no real delivery, nothing ever
// leaves the machine) — dev-only, gated the same way the realm's dev seed is, so it
// never appears in an `aspire publish`/`deploy` graph. Skipped entirely when a
// developer's own Resend key is set above: nothing ever points at it in that case,
// so starting it would just be an idle, unused container.
var mailpit = isDevRun && !useResend
    ? builder.AddContainer("mailpit", "axllent/mailpit")
        .WithHttpEndpoint(port: 8025, targetPort: 8025, name: "http")
        .WithEndpoint(port: 1025, targetPort: 1025, name: "smtp")
    : null;

// Declared before `keycloak` so ANAMNYS_APP_ORIGIN can reference this project's
// endpoint without creating a WaitFor cycle: keycloak depends on server's endpoint,
// so server cannot also WaitFor(keycloak) until keycloak exists (see below).
var server = builder.AddProject<Projects.anamnys_aspire_Server>("server")
    .WithReference(cache)
    .WithReference(anamnysDb)
    .WaitFor(cache)
    .WaitFor(anamnysDb)
    .WithEnvironment("ANAMNYS_PROVIDER_CLIENT_SECRET", providerClientSecret)
    .WithEnvironment("ANAMNYS_PATIENT_CLIENT_SECRET", patientClientSecret)
    .WithEnvironment("ANAMNYS_OWNER_CLIENT_SECRET", ownerClientSecret)
    // The appointment e-mail worker; the test fixture turns it off (SharedAppHostFixture).
    .WithEnvironment("Notifications__WorkerEnabled", builder.Configuration["Notifications:WorkerEnabled"] ?? "true")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// Fixed port, not a dynamic one: cookies and redirect URIs are bound to the
// origin, so a port that moves between AppHost restarts invalidates every
// session and every registered redirect URI.
var keycloak = builder.AddKeycloak("keycloak", 8080, keycloakAdminUsername, keycloakAdminPassword)
    .WithDockerfile("../keycloak")
    // The dev-only seeded logins, the anamnys-test-* clients and the
    // localhost:* redirect origins (see keycloak/strip-dev-seed.jq) are
    // stripped at build time unless this is true. This is the ONLY gate on
    // that content: keycloak/Dockerfile has no environment check of its own,
    // and Aspire uses this same Dockerfile for `aspire publish`/`deploy`, so
    // an ungated default would ship the credential to every environment
    // identically.
    //
    // IsRunMode is load-bearing and must not be "simplified" away.
    // builder.Environment is this AppHost *process's* hosting environment, and
    // Properties/launchSettings.json pins ASPNETCORE_ENVIRONMENT/
    // DOTNET_ENVIRONMENT to Development in both profiles — so on any developer
    // machine IsDevelopment() is true no matter what is being built, and
    // `aspire publish` would bake the dev seed into the published image.
    // ExecutionContext.IsRunMode is the publish/run discriminator: it is false
    // for `aspire publish`/`aspire deploy` regardless of the process
    // environment. Both conditions together mean the seed ships only into an
    // image built for a local `aspire run`.
    .WithBuildArg("INCLUDE_DEV_SEED", isDevRun)
    .WithPostgres(keycloakDb)
    .WithDataVolume()
    .WithOtlpExporter()
    .WithEnvironment("ANAMNYS_PROVIDER_CLIENT_SECRET", providerClientSecret)
    .WithEnvironment("ANAMNYS_PATIENT_CLIENT_SECRET", patientClientSecret)
    .WithEnvironment("ANAMNYS_OWNER_CLIENT_SECRET", ownerClientSecret)
    .WithEnvironment("ANAMNYS_APP_ORIGIN", server.GetEndpoint("http"))
    // Consumed by dev-only-overrides.jq's smtpServer block via Keycloak's own
    // ${VAR} import-time substitution (the same mechanism ${ANAMNYS_APP_ORIGIN}
    // and the client secrets above already rely on) — see useResend's own comment
    // for why this branches on a directly-read, non-mandatory configuration value
    // instead of an AddParameter. Both branches are only ever consumed when
    // INCLUDE_DEV_SEED is true, so setting them unconditionally here is harmless
    // outside a dev run — dev-only-overrides.jq never runs to read them.
    .WithEnvironment("DEV_SMTP_HOST", useResend ? "smtp.resend.com" : "mailpit")
    .WithEnvironment("DEV_SMTP_PORT", useResend ? "587" : "1025")
    .WithEnvironment("DEV_SMTP_AUTH", useResend ? "true" : "false")
    .WithEnvironment("DEV_SMTP_STARTTLS", useResend ? "true" : "false")
    .WithEnvironment("DEV_SMTP_USER", useResend ? "resend" : "")
    .WithEnvironment("DEV_SMTP_PASSWORD", useResend ? resendApiKey : "")
    // resend.dev's shared sandbox sender — works with no domain verification,
    // which fits a personal/local testing key. Swap for a verified domain's
    // address once you have one, if you want "From" to look like anamnys.dev.
    .WithEnvironment("DEV_SMTP_FROM", useResend ? "onboarding@resend.dev" : "noreply@anamnys.dev")
    // AddKeycloak only registers "http" (container :8080) and "management" (container
    // :9000) as Aspire-tracked endpoints — Keycloak's own realm/auth HTTPS listener on
    // container :8443 (see the container's own startup log: "Listening on: http://
    // 0.0.0.0:8080 and https://0.0.0.0:8443") isn't modeled at all. Reaching it matters:
    // "http" isn't published to the host at all (only reachable from a bare host process,
    // like this server project, through Aspire DCP's synthetic tunnel proxy), and that
    // tunnel has proven unreliable for anything beyond a trivial request against this
    // Keycloak image (mislabelled scheme, a corrupted TLS frame, then a response that
    // truncates mid-body for the full discovery document) — see
    // AuthenticationSetup.cs's keycloakAuthorityBase comment.
    //
    // WithHttpsEndpoint(targetPort: 8443, name: "https") looks like the fix — it makes
    // Docker publish :8443 directly, bypassing that tunnel entirely — but declaring it
    // reproducibly breaks the container's own Postgres connection at startup (it fails to
    // resolve postgres.dev.internal, even though postgres.dev.internal resolves fine for
    // every other resource and postgres is already up by the time Keycloak starts).
    // Something about how Aspire wires up an extra endpoint on this resource disturbs its
    // dependency/network timing — not yet understood, and not worth blocking this feature
    // on. WithContainerRuntimeArgs bypasses Aspire's endpoint bookkeeping completely: it's
    // a raw `docker run -p`, so Aspire never learns this port exists and the code path
    // above that breaks the container's startup never runs. Fixed 8443:8443, matching the
    // fixed-port convention the four SPA dev servers already use, since nothing here
    // tracks or reports back a dynamically-assigned one.
    .WithContainerRuntimeArgs("-p", "8443:8443")
    .WaitFor(keycloakDb);

// Not chained into the builder above: mailpit is null outside a dev run (see its
// declaration), and WaitFor has no "skip if null" overload.
if (mailpit is not null)
{
    keycloak.WaitFor(mailpit);
}

server.WithReference(keycloak).WaitFor(keycloak);

// Lets the server's outside-Development startup gate (DevOnlyTestClientGuard)
// query Keycloak's admin API for the dev-only test service-account clients.
// KEYCLOAK_ADMIN_BASE_ADDRESS is a plain endpoint reference (resolved to a
// real URL by Aspire), not the "https+http://keycloak" service-discovery
// pseudo-scheme the rest of the app uses — that scheme only resolves under
// Aspire orchestration, and the gate must not depend on it. In a real
// non-Development deployment not orchestrated by Aspire, all three of these
// come from that environment's own configuration, not from this dev
// orchestrator.
server
    .WithEnvironment("KEYCLOAK_ADMIN_USERNAME", keycloakAdminUsername)
    .WithEnvironment("KEYCLOAK_ADMIN_PASSWORD", keycloakAdminPassword)
    .WithEnvironment("KEYCLOAK_ADMIN_BASE_ADDRESS", keycloak.GetEndpoint("http"))
    // The OIDC/JwtBearer backchannel (AuthenticationSetup.cs) needs its own address,
    // separate from KEYCLOAK_ADMIN_BASE_ADDRESS above: that "http" endpoint isn't actually
    // published by Docker, so reaching it from a bare host process goes through DCP's
    // synthetic tunnel proxy, which has proven unreliable for anything past a trivial
    // request. "localhost:8443" (the raw `-p 8443:8443` published above — see that
    // comment for why it's a raw docker arg and not a named Aspire endpoint) doesn't have
    // that problem: Docker publishes it directly to a real, fixed host port, so this is a
    // plain string, not something Aspire resolves.
    .WithEnvironment("KEYCLOAK_PUBLIC_HTTPS_ADDRESS", "https://localhost:8443");

// The server's own outgoing email (portal invitations — see
// design/specs/2026-10-03-portal-invitation-design.md §5), through the same choice the
// Keycloak SMTP block above makes: the developer's Resend account when its key is set,
// otherwise Mailpit's HTTP send API (the server is a host process, so Mailpit's published
// localhost port, not the container alias). Outside a dev run nothing is configured here:
// a deployment supplies Email__* itself, and without it invitations answer 503.
if (useResend)
{
    server
        .WithEnvironment("Email__Provider", "resend")
        .WithEnvironment("Email__ResendApiKey", resendApiKey)
        .WithEnvironment("Email__From", "onboarding@resend.dev");
}
else if (mailpit is not null)
{
    server
        .WithEnvironment("Email__Provider", "mailpit")
        // An endpoint reference, not a literal localhost:8025: under Aspire.Hosting.Testing
        // DCP picks its own host port (see SharedAppHostFixture's Keycloak note).
        .WithEnvironment("Email__MailpitBaseUrl", mailpit.GetEndpoint("http"))
        .WithEnvironment("Email__From", "noreply@anamnys.dev")
        .WaitFor(mailpit);
}
if (isDevRun)
{
    // Where invitation links point: the patient app's pinned dev origin (see `patient`
    // below). A deployment serves the patient app same-origin under /patient.
    server.WithEnvironment("PatientPortal__BaseUrl", "http://localhost:5274/patient");
}

// Three SPAs, one server. Each is published into a sub-path of the server's
// wwwroot so all three stay same-origin with the API — which is what lets the
// BFF cookie auth work without CORS. The sub-paths must match the `base` option
// in each app's vite.config.ts.
// Each dev-server port is pinned (not left to Aspire's usual random assignment):
// the server builds its OIDC redirect_uri from the Host header of whatever origin
// reaches it through that app's vite.config.ts proxy, and that origin has to stay
// stable across restarts to remain a registered redirect URI in Keycloak.
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

server.PublishWithContainerFiles(web, "wwwroot");
server.PublishWithContainerFiles(provider, "wwwroot/provider");
server.PublishWithContainerFiles(patient, "wwwroot/patient");
server.PublishWithContainerFiles(admin, "wwwroot/admin");

builder.Build().Run();
