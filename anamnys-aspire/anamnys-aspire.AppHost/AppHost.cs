using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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

// Private object store for exported PDFs (design/specs/2026-10-17-note-pdf-export-design.md §4).
// SeaweedFS in S3 mode; the identity file is written from the secret parameters at start so no
// credential is ever committed (the keys must be hex/alphanumeric: they are written into JSON unescaped). -s3.encryptVolumeData
// (the S3 gateway's own flag; -filer.encryptVolumeData alone does not cover S3 uploads) encrypts the stored chunks.
// -ip/-ip.bind keep master, volume and filer on loopback; only the S3 port (8333) binds 0.0.0.0, and the
// Iceberg/Lance listeners are disabled, so other containers reach nothing but S3.
var storageAccessKey = builder.AddParameter("storage-access-key", secret: true);
var storageSecretKey = builder.AddParameter("storage-secret-key", secret: true);
var storage = builder.AddContainer("storage", "chrislusf/seaweedfs", "4.48")
    .WithVolume("anamnys-storage-data", "/data")
    .WithEnvironment("S3_ACCESS_KEY", storageAccessKey)
    .WithEnvironment("S3_SECRET_KEY", storageSecretKey)
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c", """
        printf '{"identities":[{"name":"anamnys","credentials":[{"accessKey":"%s","secretKey":"%s"}],"actions":["Admin","Read","Write","List"]}]}' "$S3_ACCESS_KEY" "$S3_SECRET_KEY" > /tmp/s3.json && exec weed server -dir=/data -ip=127.0.0.1 -ip.bind=127.0.0.1 -s3 -s3.port=8333 -s3.ip.bind=0.0.0.0 -s3.port.iceberg=0 -s3.port.lance=0 -s3.config=/tmp/s3.json -filer.encryptVolumeData -s3.encryptVolumeData -volume.max=0
        """)
    .WithHttpEndpoint(targetPort: 8333, name: "s3")
    .WithHttpHealthCheck("/healthz", endpointName: "s3");

// The browser downloads straight from the store, so presigned URLs are signed for the host it
// can reach (SigV4 signs the host). A deployment must supply that public TLS host: publish mode
// declares the required parameter `storage-public-url`, and the server refuses to start outside
// Development unless it is an absolute https URL. A dev run uses the localhost endpoint, unless
// `Parameters:storage-public-url` is set (e.g. to try a tunnel).
var storagePublicUrl = builder.Configuration["Parameters:storage-public-url"];

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

// Antivirus for external documents (design/specs/2026-10-25-external-documents-design.md §3): ClamAV's
// clamd, reached over TCP by the server only. Signatures live in the data volume and are refreshed by the
// image's freshclam, which needs outbound access to the signature mirror. The first start downloads them
// and clamd listens only once they load, so the server does not wait for it: scans retry meanwhile.
// ExternalDocuments:Scanner=fake (the test fixture) skips the container and uses the server's fake scanner.
var externalScanner = builder.Configuration["ExternalDocuments:Scanner"] ?? "clamav";
IResourceBuilder<ContainerResource>? clamav = null;
if (externalScanner == "clamav")
{
    clamav = builder.AddContainer("clamav", "clamav/clamav", "1.4.3")
        .WithVolume("anamnys-clamav-data", "/var/lib/clamav")
        .WithEndpoint(targetPort: 3310, name: "clamd", scheme: "tcp");
    var clamd = clamav.GetEndpoint("clamd");
    builder.Services.AddHealthChecks().Add(new HealthCheckRegistration("clamd_ping", _ => new ClamdHealthCheck(clamd), null, null));
    clamav.WithHealthCheck("clamd_ping");
}

// Declared before `keycloak` so ANAMNYS_APP_ORIGIN can reference this project's
// endpoint without creating a WaitFor cycle: keycloak depends on server's endpoint,
// so server cannot also WaitFor(keycloak) until keycloak exists (see below).
var server = builder.AddProject<Projects.anamnys_aspire_Server>("server")
    .WithReference(cache)
    .WithReference(anamnysDb)
    .WaitFor(cache)
    .WaitFor(anamnysDb)
    .WithEnvironment("Storage__ServiceUrl", storage.GetEndpoint("s3"))
    .WithEnvironment("Storage__AccessKey", storageAccessKey)
    .WithEnvironment("Storage__SecretKey", storageSecretKey)
    .WaitFor(storage)
    .WithEnvironment("ANAMNYS_PROVIDER_CLIENT_SECRET", providerClientSecret)
    .WithEnvironment("ANAMNYS_PATIENT_CLIENT_SECRET", patientClientSecret)
    .WithEnvironment("ANAMNYS_OWNER_CLIENT_SECRET", ownerClientSecret)
    // The appointment e-mail worker; the test fixture turns it off (SharedAppHostFixture).
    .WithEnvironment("Notifications__WorkerEnabled", builder.Configuration["Notifications:WorkerEnabled"] ?? "true")
    // Loads the LLM in the background after start; the test fixture turns it off.
    .WithEnvironment("NoteStructuring__WarmSuggestionOnStart", builder.Configuration["NoteStructuring:WarmSuggestionOnStart"] ?? "true")
    .WithEnvironment("ExternalDocuments__Scanner", externalScanner)
    // Document fact extraction with the LLM (F-08); the test fixture sets "none" so the model never loads.
    .WithEnvironment("DocumentFacts__Extractor", builder.Configuration["DocumentFacts:Extractor"] ?? "llama")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

if (clamav is not null)
{
    var clamdEndpoint = clamav.GetEndpoint("clamd");
    server
        .WithEnvironment("ExternalDocuments__ClamAvHost", clamdEndpoint.Property(EndpointProperty.Host))
        .WithEnvironment("ExternalDocuments__ClamAvPort", clamdEndpoint.Property(EndpointProperty.Port));
}

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

if (builder.ExecutionContext.IsPublishMode)
{
    server.WithEnvironment("Storage__PublicUrl", builder.AddParameter("storage-public-url"));
    // The patient app's public https address, for share links; the server refuses to start without it.
    server.WithEnvironment("PatientPortal__BaseUrl", builder.AddParameter("patient-portal-url"));
    // F-45: the RFC 3161 time stamping authority and the PEM of its root; the server refuses to start without the root.
    server.WithEnvironment("Integrity__TsaUrl", builder.AddParameter("integrity-tsa-url"));
    server.WithEnvironment("Integrity__TsaRootCertificate", builder.AddParameter("integrity-tsa-root-certificate"));
}
else if (!string.IsNullOrEmpty(storagePublicUrl))
{
    server.WithEnvironment("Storage__PublicUrl", storagePublicUrl);
}
else
{
    server.WithEnvironment("Storage__PublicUrl", storage.GetEndpoint("s3"));
}

// F-46 backup and point-in-time restore (design/specs/2026-11-05-backup-restore-design.md §2.1–2.3). Off unless
// `Parameters:backup-enabled` is true (read directly, like storage-public-url, so with it unset a dev run and the test
// AppHost get exactly the graph they had before: Aspire's postgres:18 image, no archiving, no WAL-G environment, no
// backup-agent, no backup-store, no Backup__* on the server). Publish mode always backs up, to the destination its
// required parameters name. A run with backup enabled targets a second local SeaweedFS, `backup-store`.
var backupEnabled = builder.ExecutionContext.IsPublishMode
    || string.Equals(builder.Configuration["Parameters:backup-enabled"], "true", StringComparison.OrdinalIgnoreCase);
if (backupEnabled)
{
    ReferenceExpression backupEndpoint;
    ReferenceExpression backupBucket;
    ReferenceExpression backupAccessKey;
    ReferenceExpression backupSecretKey;
    IResourceBuilder<ParameterResource> backupAgentDbPassword;
    IResourceBuilder<ContainerResource>? backupStore = null;
    if (builder.ExecutionContext.IsPublishMode)
    {
        backupEndpoint = ReferenceExpression.Create($"{builder.AddParameter("backup-s3-endpoint")}");
        backupBucket = ReferenceExpression.Create($"{builder.AddParameter("backup-s3-bucket")}");
        backupAccessKey = ReferenceExpression.Create($"{builder.AddParameter("backup-s3-access-key", secret: true)}");
        backupSecretKey = ReferenceExpression.Create($"{builder.AddParameter("backup-s3-secret-key", secret: true)}");
        backupAgentDbPassword = builder.AddParameter("backup-agent-db-password", secret: true);
    }
    else
    {
        // The local destination: same SeaweedFS image and identity-file pattern as `storage`, its own volume and its
        // own keys (generated once and kept in the AppHost's user secrets; alphanumeric, see `storage`).
        var keyDefault = new GenerateParameterDefault { MinLength = 32, Special = false };
        var storeAccessKey = builder.AddParameter("backup-store-access-key", keyDefault, secret: true, persist: true);
        var storeSecretKey = builder.AddParameter("backup-store-secret-key", keyDefault, secret: true, persist: true);
        backupStore = builder.AddContainer("backup-store", "chrislusf/seaweedfs", "4.48")
            .WithVolume("anamnys-backup-store-data", "/data")
            .WithEnvironment("S3_ACCESS_KEY", storeAccessKey)
            .WithEnvironment("S3_SECRET_KEY", storeSecretKey)
            .WithEntrypoint("/bin/sh")
            .WithArgs("-c", """
                printf '{"identities":[{"name":"backup","credentials":[{"accessKey":"%s","secretKey":"%s"}],"actions":["Admin","Read","Write","List"]}]}' "$S3_ACCESS_KEY" "$S3_SECRET_KEY" > /tmp/s3.json && exec weed server -dir=/data -ip=127.0.0.1 -ip.bind=127.0.0.1 -s3 -s3.port=8333 -s3.ip.bind=0.0.0.0 -s3.port.iceberg=0 -s3.port.lance=0 -s3.config=/tmp/s3.json -filer.encryptVolumeData -s3.encryptVolumeData -volume.max=0
                """)
            .WithHttpEndpoint(targetPort: 8333, name: "s3")
            .WithHttpHealthCheck("/healthz", endpointName: "s3");
        backupEndpoint = ReferenceExpression.Create($"{backupStore.GetEndpoint("s3")}");
        backupBucket = ReferenceExpression.Create($"anamnys-backup");
        backupAccessKey = ReferenceExpression.Create($"{storeAccessKey}");
        backupSecretKey = ReferenceExpression.Create($"{storeSecretKey}");
        backupAgentDbPassword = builder.AddParameter("backup-agent-db-password",
            new GenerateParameterDefault { MinLength = 32, Special = false }, secret: true, persist: true);
    }

    // 32 random bytes, base64 (`openssl rand -base64 32`); kept outside the server, losing it makes every backup
    // unreadable (spec §5). Required in every mode: a dev run asks for it like any unset parameter.
    var backupEncryptionKey = builder.AddParameter("backup-encryption-key", secret: true);
    var backupAlertEmail = builder.AddParameter("backup-alert-email");

    // WAL-G's environment, identical on PostgreSQL (wal-push from archive_command) and on the agent (backup-push).
    void WithWalG<T>(IResourceBuilder<T> resource) where T : IResourceWithEnvironment => resource
        .WithEnvironment("WALG_S3_PREFIX", ReferenceExpression.Create($"s3://{backupBucket}/pg"))
        .WithEnvironment("AWS_ENDPOINT", backupEndpoint)
        .WithEnvironment("AWS_REGION", "us-east-1")
        .WithEnvironment("AWS_ACCESS_KEY_ID", backupAccessKey)
        .WithEnvironment("AWS_SECRET_ACCESS_KEY", backupSecretKey)
        .WithEnvironment("AWS_S3_FORCE_PATH_STYLE", "true")
        .WithEnvironment("WALG_LIBSODIUM_KEY", backupEncryptionKey)
        .WithEnvironment("WALG_LIBSODIUM_KEY_TRANSFORM", "base64")
        .WithEnvironment("WALG_COMPRESSION_METHOD", "brotli");

    // §2.1: postgres:18 plus WAL-G (postgres/Dockerfile), WAL shipped at least every 60 s. hba_file adds replication
    // connections, which the agent's remote backup-push needs. `timeout 60`: with the destination unreachable WAL-G
    // retries without end, which hangs shutdown (a fresh volume's init server never stops); bounded, a hung push fails
    // and PostgreSQL retries the segment later. 60 s still fits a 16 MB segment over a slow link.
    postgres
        .WithDockerfile("../postgres")
        .WithArgs("-c", "archive_mode=on", "-c", "archive_command=timeout 60 wal-g wal-push %p", "-c", "archive_timeout=60",
            "-c", "wal_level=replica", "-c", "hba_file=/etc/postgresql/anamnys/pg_hba.conf");
    WithWalG(postgres);
    // The entrypoint's pg_ctl stop (fresh-volume init) waits 60 s by default; the archiver may retry a failing segment
    // 3 times at shutdown, each bounded by the 60 s timeout above, so give it 300 s.
    postgres.WithEnvironment("PGCTLTIMEOUT", "300");
    if (backupStore is not null)
    {
        postgres.WaitFor(backupStore);
    }

    // §2.3: the agent connects as the superuser (WAL-G requires it for backup-push; it also gives
    // anamnys_backup_agent its login) and writes BackupRuns as anamnys_backup_agent.
    var postgresEndpoint = postgres.Resource.PrimaryEndpoint;
    var backupAgent = builder.AddDockerfile("backup-agent", "../backup-agent")
        .WithEnvironment("PGHOST", postgresEndpoint.Property(EndpointProperty.Host))
        .WithEnvironment("PGPORT", postgresEndpoint.Property(EndpointProperty.Port))
        .WithEnvironment("PGUSER", postgres.Resource.UserNameReference)
        .WithEnvironment("PGPASSWORD", ReferenceExpression.Create($"{postgres.Resource.PasswordParameter}"))
        .WithEnvironment("ANAMNYS_DB", anamnysDb.Resource.DatabaseName)
        .WithEnvironment("KEYCLOAK_DB", keycloakDb.Resource.DatabaseName)
        .WithEnvironment("BACKUP_AGENT_DB_PASSWORD", backupAgentDbPassword)
        .WithEnvironment("SOURCE_S3_ENDPOINT", storage.GetEndpoint("s3"))
        .WithEnvironment("SOURCE_S3_ACCESS_KEY", storageAccessKey)
        .WithEnvironment("SOURCE_S3_SECRET_KEY", storageSecretKey)
        // The server's bucket (StorageOptions.Bucket's default; the AppHost never overrides it).
        .WithEnvironment("SOURCE_S3_BUCKET", "notes")
        .WithEnvironment("BACKUP_S3_ENDPOINT", backupEndpoint)
        .WithEnvironment("BACKUP_S3_BUCKET", backupBucket)
        .WithEnvironment("BACKUP_S3_ACCESS_KEY", backupAccessKey)
        .WithEnvironment("BACKUP_S3_SECRET_KEY", backupSecretKey)
        .WithEnvironment("BACKUP_ENCRYPTION_KEY", backupEncryptionKey)
        .WaitFor(anamnysDb)
        .WaitFor(keycloakDb)
        .WaitFor(storage);
    WithWalG(backupAgent);
    if (backupStore is not null)
    {
        backupAgent.WaitFor(backupStore);
    }

    // §2.4: the server's BackupMonitor runs only with Backup:Enabled.
    server
        .WithEnvironment("Backup__Enabled", "true")
        .WithEnvironment("Backup__AlertEmail", backupAlertEmail);
}

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

// A dev run timestamps with the public FreeTSA and its root (integrity/freetsa-cacert.pem, from
// https://freetsa.org/files/cacert.pem, valid until 2041), so manual testing shows anchoring. Only the chain head hash
// leaves the machine. Parameters:integrity-tsa-url / Parameters:integrity-tsa-root-certificate override both.
if (isDevRun)
{
    server.WithEnvironment("Integrity__TsaUrl",
        builder.Configuration["Parameters:integrity-tsa-url"] is { Length: > 0 } tsaUrl ? tsaUrl : "https://freetsa.org/tsr");
    server.WithEnvironment("Integrity__TsaRootCertificate",
        builder.Configuration["Parameters:integrity-tsa-root-certificate"] is { Length: > 0 } tsaRoot
            ? tsaRoot
            : File.ReadAllText(Path.Combine(builder.AppHostDirectory, "integrity", "freetsa-cacert.pem")));
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
