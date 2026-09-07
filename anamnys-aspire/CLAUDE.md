# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this directory is

`anamnys-aspire/` is **the application** — a single Aspire-orchestrated solution and the
only source tree. There is no other project directory.

```
anamnys/                     <- git root (branch: develop)
└── anamnys-aspire/          <- THIS DIR: the whole project
    ├── anamnys-aspire.AppHost/   AppHost.cs — resource graph
    ├── anamnys-aspire.Server/    minimal API + Extensions.cs (service defaults)
    ├── apps/                     four Vite + React + TanStack Router SPAs:
    │                             web, provider, patient, admin
    ├── apps/keycloak-theme/      Keycloakify theme for the Keycloak login pages
    ├── keycloak/                 realm JSON, Dockerfile, theme build output
    └── design/specs/             design documents
```

An earlier `backend/` (ASP.NET Core) and `frontend/` (Next.js) pair existed at the git root
and was **deleted in `aee5233`**. Do not look for them, cite them, or restore code from
them. Anything worth keeping was already migrated here; anything not here is not part of
the project.

## The product

Anamnys is a **specialty-native clinical note drafting system** for solo/small-group mental
health and physical therapy practices. Provider dictates audio → Whisper transcribes →
LLaMA structures into a clinical note (SOAP/DAP) → billing engine derives CPT/ICD-10 codes
with a denial-risk score → provider reviews, signs, exports to PDF.

### HIPAA constraints — treat as hard requirements

These are binding product requirements, not observations about existing code. Most are
not yet visible in the tree — the server is still close to the Aspire template — so do
not read their absence as evidence they are optional or aspirational. They constrain
every feature built from here.

- **All AI inference runs in-process on the same server.** Whisper.net (STT) and LLamaSharp
  (structuring) are .NET libraries, not sidecars. **Never** propose routing patient audio,
  transcripts, or notes to a cloud AI API (OpenAI, Azure OpenAI, Gemini, Anthropic) — doing
  so puts PHI outside the network boundary and requires a BAA.
- **Everything self-hosted.** The same rule governs every dependency that touches PHI:
  database, identity provider, object storage. Managed/hosted services are not an option
  for PHI-bearing components without a signed BAA.
- **Provider-scoping is enforced at the query level.** The authenticated user's id is
  resolved server-side and appended to every database query, so a provider cannot read
  another provider's patients or notes even with a valid session. This is the system's
  primary access control — any new data-access code must preserve it.
- Every note mutation appends an `AuditEntry` to the `AuditTrail` JSONB column (§164.312(b)).
- Exported PDFs are ePHI: private bucket, access only via 1-hour presigned URLs.
- **The application never stores credentials.** Keycloak owns passwords, hashing, reset
  flows, and lockout. There is no password column in the application database.
- **No PHI in tokens, logs, or URLs.** Tokens carry subject, roles, and scopes only.

## Tech Stack

Split into what is **wired up today** and what is **planned**. Do not write code against a
planned package without adding the reference first.

### Backend — present

- .NET 10, ASP.NET Core Minimal APIs, C# 14
- Redis (Aspire-managed container) for output caching and the BFF ticket store
- OpenTelemetry via `Extensions.cs` service defaults
- Entity Framework Core 10.0
- PostgreSQL 18 — runs as an **Aspire-managed Docker container**, both locally and in
  production. Not a hosted service (see HIPAA constraints).
- Keycloak for identity — three realms (providers, patients, owners), seeded from
  committed realm JSON baked into a custom image at `keycloak/Dockerfile`. See
  `design/specs/2026-09-05-keycloak-implementation-design.md`.

### Backend — planned

- FluentValidation for request validation
- Scalar for OpenAPI documentation
- xUnit + FluentAssertions for testing

### Frontend — present

- Four Vite + React SPAs — `apps/web`, `apps/provider`, `apps/patient`, `apps/admin` —
  each same-origin with the API via its own sub-path of the server's `wwwroot`.
- Keycloakify (`apps/keycloak-theme`) compiles a React login page into the Keycloak
  image; the Authorization Code + PKCE redirect itself is untouched — only the
  rendered page is ours.
- React 19, Vite 8
- TypeScript **6.0.3** — see the TypeScript version note below
- TanStack Router (file-based routing)
- TanStack Query (server state and caching)
- Tailwind CSS 4
- Zustand (client state — UI and session, never tokens)
- i18next / react-i18next
- `@microsoft/signalr` for the progress and transcription hubs

### Frontend — migrating away from

Both are present in the tree today and both are contained to a single file, so replace
them opportunistically rather than in a dedicated pass.

- **`axios` → native `fetch`.** Confined entirely to `src/api/client.ts`; no `Axios*` type
  escapes it, and the seven `src/api/*.ts` modules only consume the default instance. Note
  TanStack Query is *not* an HTTP client — it is an async-state and caching layer — so the
  replacement is Query plus native `fetch`, not Query alone. When rewriting `client.ts`,
  **`credentials: 'include'` is mandatory** (it replaces axios's `withCredentials: true`);
  omitting it silently breaks the HttpOnly cookie the BFF auth depends on. Preserve the
  response interceptor's error normalisation too.
- **`@base-ui/react` → shadcn/ui.** Used in exactly one file,
  `src/components/ui/alert-dialog.tsx`. Prefer shadcn for any new component.

## Common Commands

**Docker must be running** — the AppHost starts container-backed resources: Redis,
Postgres, and Keycloak. **Apache Maven must also be installed** — `keycloakify build`
shells out to it to package the login theme.

### First-time setup on a new machine

Four AppHost parameters are declared `secret: true` with no default, so they have no
value until you set one. User secrets live in `~/.microsoft/usersecrets/<UserSecretsId>/`
and are deliberately never committed, so **every developer sets their own on every
machine** — a working checkout on someone else's laptop does not carry them across.

Without them, `aspire run` stops and asks you to resolve the parameters.

`dotnet user-secrets` reads `<UserSecretsId>` from the `.csproj` in the current
directory, so it must run against the AppHost project — `anamnys-aspire.AppHost/`,
the inner directory, not the `anamnys-aspire/` solution directory above it. Passing
`--project` avoids depending on where you are:

```bash
# from anamnys-aspire/ (the solution directory)
P=anamnys-aspire.AppHost
dotnet user-secrets --project $P set "Parameters:provider-client-secret"  "$(openssl rand -hex 32)"
dotnet user-secrets --project $P set "Parameters:patient-client-secret"   "$(openssl rand -hex 32)"
dotnet user-secrets --project $P set "Parameters:owner-client-secret"     "$(openssl rand -hex 32)"
dotnet user-secrets --project $P set "Parameters:keycloak-admin-password" "$(openssl rand -hex 32)"

# check what landed (values are shown, so not in a shared terminal)
dotnet user-secrets --project $P list
```

**Do not copy these values from a teammate.** Each of the three client secrets is read in
exactly two places that must agree *with each other on one machine* and nowhere else:
Keycloak substitutes it into the realm JSON as `${ANAMNYS_*_CLIENT_SECRET}` at import, and
the server uses it for the token exchange. Nothing shared or deployed depends on your
value, so generating your own is both sufficient and safer than passing credentials around.

`Parameters:keycloak-admin-username` needs nothing — it defaults to `"admin"` in
`AppHost.cs`. `postgres-password`, `keycloak-password` and `cache-password` are generated
by Aspire on first run.

**If you have already run the AppHost once before setting these**, wipe the data volumes
before your next start. Realm import only runs when the realm does not exist, so Keycloak
still holds the client secrets from that earlier run and will reject the new ones — and it
fails as an opaque client-authentication error, not as anything naming the mismatch:

```bash
aspire stop
docker volume ls | grep -E 'keycloak-data|postgres-data'
docker volume rm <keycloak-data>    # one at a time; passing both names can fail
docker volume rm <postgres-data>
```

### Aspire (from this directory)

```bash
aspire start        # background start — the form agents should use
aspire run          # foreground + dashboard — for a human at the terminal
aspire stop         # do this BEFORE any rebuild
aspire doctor
```

A running Aspire app holds file locks on `bin/` and `obj/`. An `MSB3491` or `CS2012` during
a build means "run `aspire stop` first" — not that the project is broken.

### Frontend (from `apps/<web|provider|patient|admin|keycloak-theme>/`, or the repo root
for all workspaces at once)

```bash
npm run dev     # vite (per app)
npm run build   # tsc -b && vite build — root runs this across all workspaces
npm run lint    # eslint — root runs this across all workspaces
```

## Architecture

- Clean Architecture for the backend API.
- The four SPAs (`web`, `provider`, `patient`, `admin`) are served by the .NET server, each
  from its own sub-path of `wwwroot` (`/`, `/provider/`, `/patient/`, `/admin/`).
  `AddViteApp` + `PublishWithContainerFiles` bakes each app's built assets into that
  sub-path, so there is **one container in production** and every app is same-origin with
  the API. This is deliberate: it is the only frontend shape in Aspire 13.5.2 with **zero
  experimental APIs**, it keeps PHI off any Node runtime, and same-origin is what makes the
  cookie-based auth design work. Do not replace it with an SSR framework without revisiting
  all three consequences.
- Authentication uses a **Backend-for-Frontend**: the .NET server holds tokens, the browser
  holds only an `HttpOnly` `SameSite=Strict` session cookie. Tokens never enter JavaScript.

## Gotchas

- **Each app's `src/routeTree.gen.ts` (`apps/web`, `apps/provider`, `apps/patient`,
  `apps/admin`) is generated but MUST stay committed.** `npm run build` runs `tsc -b`
  *before* Vite generates the route tree, so a clean checkout fails without it. If one is
  ever missing, regenerate it with a bare `npx vite build` in that app's directory.
- **Aspire CLI version.** `AspireUseCliBundle` makes the launcher run a bundle matching the
  AppHost SDK version. Trust `aspire --version`, never the Homebrew install path.
- **TypeScript is held at 6.0.3, not the latest 7.0.2.** No released or canary
  `typescript-eslint` supports TypeScript 7 — the newest peer range is
  `>=4.8.4 <6.1.0` — so upgrading breaks `npm run lint`. Two pins keep this stable:
  `typescript` is `~6.0.3` (patch-only, so `npm install` cannot pull 6.1+), and
  `typescript-eslint` must stay `>=8.68.0`, the first release whose peer range admits
  TypeScript 6 at all (8.48.x capped at `<6.0.0`). Revisit both together when
  typescript-eslint ships TS 7 support.
- **The `overrides` block in the root `package.json` is security-relevant, not cosmetic.**
  It pins transitive dependencies away from known advisories (`js-yaml`,
  `brace-expansion`, `nanoid`, `postcss`, `esbuild`) for every workspace at once. Pinning to
  an *exact* version means the pin itself goes stale and starts holding the tree at a
  vulnerable version — check `npm audit` after changing it, and bump the pins rather than
  removing them.
- **`anamnys-aspire/apps/*/obj*` is gitignored.** The esproj SDK emits a directory whose
  name contains a literal backslash on macOS. Committing it would make `git clone` fail on
  Windows, where `\` is an illegal filename character.
- **`dotnet test` does not work in this solution — it is a trap, not a shortcut.** It fails
  with a misleading VSTest-style error. The real gate is
  `Microsoft.Testing.Platform.MSBuild`'s `_SupportsGlobalJsonTestRunner` check, which
  requires a `global.json` test-runner opt-in that was deliberately removed from this repo.
  **Use `dotnet run --project anamnys-aspire.Tests` instead**, and filter with
  `-- -method "*Pattern*"` when you only want a subset.
- **Apache Maven is a build prerequisite for the Keycloakify theme**, alongside "Docker
  must be running" above — `keycloakify build` shells out to `mvn` to produce the theme
  jar. This is not a Dockerfile concern: `keycloak/Dockerfile` only `COPY`s an
  already-built jar, it never builds one.
- **The Keycloak image needs the theme jar to exist before it builds.**
  `npm run build-keycloak-theme -w @anamnys/keycloak-theme` writes
  `keycloak/theme/keycloak-theme-anamnys.jar`, which the Dockerfile `COPY`s. The jar is
  gitignored. A missing jar fails the image build, which is deliberate: the alternative is
  a Keycloak serving the stock login page and nobody noticing.
- **Realm import only runs when the realm does not already exist.** `keycloak` uses
  `WithDataVolume()` (and so does `postgres` — Keycloak's realm data actually lives in the
  Postgres volume, not a Keycloak-owned one), so editing `keycloak/realms/*.json` changes
  nothing until both volumes are removed. The failure is silent: Keycloak starts healthy,
  just with the *old* realm. Recover with `aspire stop`, then `docker volume rm` on the
  Keycloak data volume and the Postgres data volume — remove them one at a time; passing
  both names to a single `docker volume rm` invocation may fail on some Docker daemons —
  then `aspire start`.
- **`INCLUDE_DEV_SEED` is the only gate stopping dev credentials from reaching
  production.** `keycloak/Dockerfile` runs a `jq` filter (`strip-dev-seed.jq`) that removes
  the `users` array and every `localhost` redirect/logout/web-origin entry from each realm
  file unless `INCLUDE_DEV_SEED=true`; `AppHost.cs` only passes `true` in Development. The
  committed realm JSON contains real dev-only credentials (`dev.provider`, `dev.owner`) —
  this filter is the entire reason they never ship. Anyone editing the Dockerfile or the
  realm JSON must not weaken or bypass it.
- **Cookies are distinguished by name, not path.** `__Host-anamnys-provider`, `-patient`,
  and `-owner` all live at `Path=/`. Scoping them to `/provider/` etc. looks tidier and
  breaks everything, because API calls go to `/api/*` and the cookie would not be sent.
- **A wrong-realm credential must produce 401, not 403.** Endpoint groups list their
  accepted authentication schemes; an unlisted realm is not an authenticated principal
  there at all. If a test starts returning 403, a scheme has leaked into a group it should
  not be in — fix the group, never the test.
- **The dev Vite ports (5273 provider, 5274 patient, 5275 web, 5276 admin) are pinned, and
  that is load-bearing, not tidiness.** They appear in three places that must all agree:
  `AppHost.cs`, each app's `vite.config.ts`, and the Keycloak realm JSON's redirect URIs.
  Change one without the other two and login breaks with a redirect-URI mismatch.
- **Each app's Vite proxy omits `changeOrigin` for `/auth` and `/signin-oidc-*`, but sets
  it `true` for `/api` and `/hubs` — on purpose.** The server builds its OIDC redirect_uri
  from the Host header it sees, so `/auth` and the sign-in callback must reach it looking
  like the browser's own origin (e.g. `localhost:5273`), while `/api` and `/hubs` should
  look like the container-network origin. Making them symmetric builds redirect URIs
  against the wrong origin and lands the session cookie somewhere the browser never asks.
- **`http://localhost` is treated as a secure context by the browser; a container network
  alias like `aspire.dev.internal` is not.** This is why dev auth works over plain HTTP on
  `localhost` and silently fails on the container alias — `Secure` cookies get dropped
  before they ever reach JavaScript or DevTools' obvious notice.
- **Outside Development, the server refuses to start without two settings, and both gates
  are deliberate, not bugs:** `DataProtection:CertificateThumbprint` (data-protection keys
  must be encrypted at rest outside dev) and `KEYCLOAK_ADMIN_BASE_ADDRESS` (needed so the
  startup guard can query Keycloak's admin API for the dev-only test clients and refuse to
  boot if they still exist). Missing either throws `InvalidOperationException` at startup.
- **`Parameters:keycloak-admin-password` must exist as a user secret in
  `anamnys-aspire.AppHost`** — it is one of four such parameters; see "First-time setup
  on a new machine" above for the full set and the volume caveat. Without it the AppHost
  will not start on a fresh clone, because it is
  declared `secret: true` with no default, unlike `keycloak-admin-username`, which
  defaults to `"admin"` in `AppHost.cs` and needs no secret at all. Both are set
  explicitly (rather than left to `AddKeycloak`'s own bootstrap defaults) because the
  server's outside-Development startup gate needs a stable, resolvable admin credential.
- **`anamnys-db-script.sql` had five pre-existing defects that meant it had never once
  successfully replayed against a fresh PostgreSQL:** a `CREATE SCHEMA "public"` (already
  exists on a fresh database and errors), 69 redundant `_pkey` indexes, 28 redundant
  `_key`/`_unique` indexes, two malformed `gist` overlap-exclusion statements, and a
  `TABLESPACE public` clause on a view (views don't take a tablespace). All are fixed in
  the committed file. **If this file is ever regenerated from whatever tool produced it,
  all five defects will come back** — do not blindly overwrite it from that tool again.
- **Keycloak owns TOTP enrolment, password reset, and recovery codes.** There is no 2FA
  settings UI in any SPA — link out to the Keycloak account console instead (see
  `apps/provider/src/routes/_app/settings/account.tsx`).
- **Aspire's `WithRealmImport` (Keycloak) is development-only** and is silently dropped by
  `aspire publish`/`deploy`. Production realm seeding needs a custom image — see
  `keycloak/Dockerfile` above.

## Workflow

- Branch naming: `feature/`, `bugfix/`, `hotfix/`
- Commit format: `type: description` (feat, fix, refactor, test, docs, chore)
- Always create a branch before changes
- Run tests before committing

### Creating/Modifying API Endpoints

1. Plan the endpoint changes — new/updated methods, paths, request payloads
2. Confirm proposed changes with the user
3. Implement the endpoint
4. Add/update the endpoint in `anamnys-aspire.Server/anamnys-aspire.Server.http`, documenting
   the endpoint and its payloads
5. Run the requests in that `.http` file against a running AppHost to verify
6. Choose the endpoint group deliberately: `/api/phi/*` for anything touching patient
   data, `/api/admin/*` for owners-realm surfaces. The group determines which realms can
   authenticate at all — see `design/specs/2026-09-05-keycloak-implementation-design.md` §2.

## Code Style

- General:
  - Prefer writing clear code and use inline comments sparingly
- C#:
  - 4-space indent
  - `PascalCase` for classes/methods
  - `_camelCase` for private fields
  - `camelCase` for local variables, parameters
  - Primary constructors, including for DI
  - File-scoped namespaces
  - Always pass `CancellationToken` to async methods
  - Use records for complex incoming request parameters
  - Use auto-properties, and `field` if necessary
  - No XML documentation comments
- Tests:
  - `<ClassName>Tests` for test class
  - `<MethodName>_<Conditions>_<AssertedOutcome>` for test methods (never `Async` suffix)
  - Arrange, Act, Assert pattern (comment each section in method)
- TypeScript/JavaScript/CSS:
  - 2-space indent (matches the existing code)
  - Keep `*.test.ts` files in the same directory as the corresponding `*.ts` file

### Patterns We DON'T Use (Never Suggest)

- Repository pattern (use EF Core directly)
- AutoMapper (write explicit mappings)
- Exceptions for business logic errors
- Stored procedures
