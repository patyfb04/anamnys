# Chapter 7: Keycloak in Practice

Chapter 6 covered *why* the system is shaped the way it is. This chapter covers the actual
mechanics: the files in `keycloak/`, how a realm gets from committed JSON into a running
Keycloak instance, and the handful of gotchas that catch developers who haven't internalized
how the pieces fit together.

## The realm files

```
keycloak/
├── Dockerfile
├── strip-dev-seed.jq
├── realms/
│   ├── anamnys-providers.json
│   ├── anamnys-patients.json
│   └── anamnys-owners.json
└── theme/                    (build output — gitignored, see Chapter 15)
```

Each realm JSON is **committed to git and treated as reviewed source** — this is a
deliberate design choice, not an accident of how Keycloak happens to export configuration.
The reasoning from the design spec is worth keeping in mind: a realm's MFA requirement or
password policy becomes visible in a code review diff, which is itself a defensible HIPAA
control (you can point to a specific commit and reviewer for any change in authentication
posture). Looking inside `anamnys-providers.json`, you'll find the realm name
(`anamnys-providers`), its client list (`anamnys-api`, `anamnys-web`, and — in
Development builds only — `anamnys-test-provider`), its realm roles (`provider`,
`clinic-admin`, `billing-staff`), and, again only in dev builds, a seeded `dev.provider`
user. The patients and owners realm files follow the same shape, with the role and client
lists Chapter 6 described.

**The one hard rule for these files: no client secrets ever get committed.** Instead, the
realm JSON references `${ANAMNYS_PROVIDER_CLIENT_SECRET}`-style placeholders, which get
substituted at import time from environment variables the AppHost injects (Chapter 4) —
the same values you set as user secrets in Chapter 3. If you ever export a realm from
Keycloak's admin UI to hand-edit it, be aware that admin-UI exports are noisy and can
embed live secrets; curate carefully before committing anything you didn't hand-write.

## The build: filtering happens before Keycloak ever sees the file

```dockerfile
ARG INCLUDE_DEV_SEED=false

FROM alpine:3 AS realms
RUN apk add --no-cache jq
COPY realms/ /in/
ARG INCLUDE_DEV_SEED
COPY strip-dev-seed.jq /strip-dev-seed.jq
RUN set -e; mkdir -p /out && for f in /in/*.json; do \
      if [ "$INCLUDE_DEV_SEED" = "true" ]; then \
        cp "$f" "/out/$(basename "$f")"; \
      else \
        jq -f /strip-dev-seed.jq "$f" > "/out/$(basename "$f")"; \
      fi; \
    done

FROM quay.io/keycloak/keycloak:26.6
COPY --from=realms /out/ /opt/keycloak/data/import/
COPY ./theme/keycloak-theme-anamnys.jar /opt/keycloak/providers/
```

Two things worth noticing in this file that aren't obvious from a casual read:

**Filtering happens in its own build stage, before Keycloak's importer ever touches the
files** — not as a check Keycloak or the application runs at startup. This matters because
a runtime check would mean the dev credentials are *live inside the built image*, and
briefly active in a running Keycloak, even if something downstream later refused to use
them. Filtering at build time means the sensitive content never exists in the shipped
artifact at all. The Alpine stage exists purely to host `jq`, which isn't present on the
UBI-based Keycloak image itself.

**`set -e` inside the loop is deliberate, not boilerplate.** Without it, a `jq` failure on
one realm file wouldn't fail the whole build — only the loop's last iteration's exit status
would matter, silently letting a truncated or empty realm file into the image. This is the
kind of defensive detail that's easy to skip and expensive to be missing.

`INCLUDE_DEV_SEED` defaults to `false` in this Dockerfile itself — not just in
`AppHost.cs`. That's intentional belt-and-suspenders: this Dockerfile is the *one* build
path Aspire uses for both `aspire start` (dev) and `aspire publish`/`aspire deploy`
(everywhere else), so anything not explicitly gated here would ship to every environment
identically. `AppHost.cs` only passes `--build-arg INCLUDE_DEV_SEED=true` when
`ExecutionContext.IsRunMode && Environment.IsDevelopment()` both hold — see Chapter 4 for
why that specific combination, rather than `IsDevelopment()` alone, is required.

## What `strip-dev-seed.jq` actually removes

Reading the filter script directly is worth doing once, because it names precisely what's
considered too sensitive to ship:

- **`.users`** — the entire seeded-users array, which is what makes `dev.provider` and
  `dev.owner` interactively loginable in local development.
- **Every client whose id starts with `anamnys-test-`** — `anamnys-test-provider` and
  `anamnys-test-owner`. These exist purely so integration tests (Chapter 16) can obtain
  real tokens via the `client_credentials` grant, because every *real* BFF client has that
  grant disabled entirely. Each test client's secret (`"test-only-not-a-secret"`) is
  committed in plain sight in the unfiltered realm JSON — deliberately unmistakable as a
  non-secret, but still something that must never reach a real deployment, because
  possessing it would let anyone who can read this repository mint a valid token.
- **Every `localhost` entry** in each client's `redirectUris`, `webOrigins`, and the
  `##`-delimited `post.logout.redirect.uris` attribute — these exist so each SPA's local
  Vite dev-server proxy can complete an OIDC redirect against its own dev port. The
  wildcard `webOrigins: ["+"]` (Keycloak's own "trust whatever's in redirectUris" marker)
  is deliberately left alone; only literal `localhost` entries are stripped. Templated
  `${ANAMNYS_APP_ORIGIN}` entries — the real, environment-supplied production origin — are
  also left untouched.
- **`sslRequired` is tightened to `"all"`.** The committed dev value, `"external"`, tells
  Keycloak it's fine to serve login and token endpoints over plain HTTP to anything it
  considers a private peer — which sounds reasonable until you notice that behind a
  TLS-terminating ingress (the normal shape of a real deployment), *every* peer looks
  private to Keycloak. Development genuinely needs `"external"` because the whole flow runs
  over plain `http://localhost`; this filter is already the dev/production discriminator,
  so it's the natural place to also flip this setting for everywhere that isn't dev.

`DevOnlyTestClientGuard` (Chapter 8) checks for exactly these same things — the test
clients, the seeded users, localhost origins — at server startup outside Development. It
is explicitly **defense in depth, not the primary control**: by the time that guard runs,
Keycloak has already imported and activated whatever was in the image. If this jq filter
ever regresses, the guard is what catches it before real traffic is served — but the filter
is what's supposed to prevent the problem from existing in the first place.

## The port and volume gotchas

You met both of these briefly in Chapters 3 and 4; here's the full picture now that you
know how the pieces connect.

**Keycloak's port (8080) is fixed, not dynamically assigned**, for the same reason the four
SPA dev ports are fixed: cookies and registered redirect URIs are bound to a specific
origin, and that origin has to survive an AppHost restart.

**Realm import only runs when the realm doesn't already exist**, and — because both
Keycloak and Postgres use `WithDataVolume()` — that "already exists" check survives an
`aspire stop`/`aspire start` cycle. (Worth remembering explicitly: Keycloak's realm data
physically lives in the Postgres volume, not a Keycloak-owned one, because `WithPostgres
(keycloakDb)` points it there.) The practical consequence: if you edit
`keycloak/realms/anamnys-providers.json` — add a role, change a redirect URI, adjust a
password policy — **nothing changes in your running environment** until you remove the
underlying volumes and let import run fresh. The failure mode is silent in the worst way:
Keycloak starts up healthy, its own readiness check reports fine, and it's serving the
*old* realm the whole time. The recovery sequence is the same one from Chapter 3:

```bash
aspire stop
docker volume ls | grep -E 'keycloak-data|postgres-data'
docker volume rm <keycloak-data>
docker volume rm <postgres-data>
```

## Local HTTP and the "secure context" trap

One more environment-specific gotcha worth knowing before you hit it: `http://localhost` is
treated as a secure context by every modern browser, even without TLS — but a container
network alias (something like `aspire.dev.internal`) is not, even if you're accessing it in
the exact same way. This is why the dev auth flow works fine over plain HTTP against
`localhost` and silently fails against a container-network alias: `Secure`-flagged cookies
(and all three of this system's session cookies are `Secure`, per Chapter 6) get dropped by
the browser before they ever reach JavaScript or become visible in DevTools' obvious
network inspector view. If login mysteriously "just doesn't set a cookie" and you're not
on `localhost`, this is the first thing to check.

## The theme jar dependency

The Dockerfile's last line, `COPY ./theme/keycloak-theme-anamnys.jar
/opt/keycloak/providers/`, assumes a file that doesn't exist until a separate build step
produces it — `npm run build-keycloak-theme -w @anamnys/keycloak-theme`. That jar is
gitignored (it's a build artifact, not source), and the Dockerfile has no fallback: a
missing jar fails the image build outright, rather than quietly falling back to Keycloak's
stock login page. That's deliberate — the alternative is a production Keycloak silently
serving generic login UI with nobody noticing. Chapter 15 covers the theme app and its
build pipeline in full.

Chapter 8 now goes back to the server side, and walks through exactly what happens in C#
from the moment a browser hits `/auth/provider/login` to the moment a request carries a
usable `CurrentUser.LocalId`.
