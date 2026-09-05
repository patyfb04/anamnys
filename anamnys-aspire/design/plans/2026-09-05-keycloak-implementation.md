# Keycloak Authentication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A provider signs in through a custom React login page served by Keycloak, and lands in the app with a `Providers` row provisioned from their Keycloak subject, holding only an opaque session cookie.

**Architecture:** Keycloak runs as an Aspire-managed container built from a custom Dockerfile that bakes in three realm JSON files and a Keycloakify-built React login theme. The .NET server is a Backend-for-Frontend: three cookie authentication schemes (one per realm, distinguished by cookie name), three OpenID Connect handlers, and three JWT bearer schemes. Access and refresh tokens live in a Redis-backed `ITicketStore`; the browser only ever sees an opaque session id. Endpoint groups name their accepted schemes explicitly, so a wrong-realm credential fails authentication rather than authorization.

**Tech Stack:** .NET 10 / ASP.NET Core Minimal APIs / C# 14, Aspire 13.5.3, Keycloak 26.6, PostgreSQL 18, EF Core 10 + Npgsql, Redis (existing), React 19 + Vite 8 + TanStack Router, Keycloakify 11, xUnit + FluentAssertions.

**Spec:** `anamnys-aspire/design/specs/2026-09-05-keycloak-implementation-design.md`, which extends `anamnys-aspire/design/specs/2026-08-24-authentication-design.md`. Read both before starting; this plan argues from them and does not restate their reasoning.

---

## Global Constraints

Every task's requirements implicitly include this section.

- **Working directory is `anamnys-aspire/`** for all commands unless a step says otherwise. The git root is its parent.
- **Branch:** `feature/keycloak-auth`, already created. Do not work on `develop`.
- **`aspire stop` before any `dotnet build`.** A running AppHost holds file locks on `bin/`/`obj/`; `MSB3491` or `CS2012` means "stop the AppHost first", not "the project is broken".
- **Docker must be running.**
- **Exact package versions**, verified against nuget.org on 2026-09-05:
  - `Aspire.Hosting.Keycloak` `13.5.3-preview.1.26425.3`
  - `Aspire.Keycloak.Authentication` `13.5.3-preview.1.26425.3`
  - `Aspire.Hosting.PostgreSQL` `13.5.3`
  - `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL` `13.5.3`
  - `CommunityToolkit.Aspire.Hosting.Keycloak.Extensions` `13.5.1-beta.736`
  - `Microsoft.AspNetCore.Authentication.OpenIdConnect` `10.0.11`
  - `Microsoft.AspNetCore.Authentication.JwtBearer` `10.0.11`
  - `Microsoft.AspNetCore.DataProtection.StackExchangeRedis` `10.0.11`
  - `Microsoft.EntityFrameworkCore.Design` `10.0.11`
  - `Npgsql.EntityFrameworkCore.PostgreSQL` `10.0.3`
  - `xunit.v3` `3.1.1`, `FluentAssertions` `8.8.0`, `Aspire.Hosting.Testing` `13.5.3`
- **TypeScript stays at `~6.0.3` and `typescript-eslint` at `>=8.68.0`** in the three existing SPAs and any new one. `apps/keycloak-theme` is the one permitted exception and only if Task 1 proves it necessary.
- **No PHI in token claims, logs, or URLs.** Subject, roles, and scopes only.
- **No client secrets in committed realm JSON.** Use `${ENV_VAR}` placeholders fed by Aspire parameters.
- **C# style** (from `CLAUDE.md`): 4-space indent, file-scoped namespaces, primary constructors including for DI, `_camelCase` private fields, `CancellationToken` on every async method, no XML doc comments, records for complex request parameters.
- **Test naming:** `<ClassName>Tests`, `<MethodName>_<Conditions>_<AssertedOutcome>` with no `Async` suffix; Arrange/Act/Assert with a comment per section.
- **Never use:** repository pattern, AutoMapper, exceptions for business logic, stored procedures.
- **TypeScript/CSS:** 2-space indent.

---

## File Structure

**New C# — `anamnys-aspire.Server/`**

| File | Responsibility |
|---|---|
| `Auth/AuthSchemes.cs` | Scheme name constants and the comma-joined scheme lists endpoint groups use |
| `Auth/Realms.cs` | Realm name constants |
| `Auth/AuthenticationSetup.cs` | Registers 3 cookie + 3 OIDC + 3 bearer schemes |
| `Auth/RedisTicketStore.cs` | `ITicketStore` over Redis, data-protected at rest |
| `Auth/TokenRefresher.cs` | `OnValidatePrincipal` refresh-token exchange |
| `Auth/FirstLoginProvisioner.cs` | Resolves or creates the local row from the token subject |
| `Auth/CurrentUser.cs` | Reads the resolved local id off the principal; the only sanctioned source of a provider id |
| `Auth/AuthEndpoints.cs` | `/auth/{realm}/login`, `/logout`, `/api/auth/me` |
| `Data/AnamnysDbContext.cs` | EF Core context |
| `Data/Entities/*.cs` | `Provider`, `PatientAccount`, `Staff`, `BreakGlassGrant`, `AccessLog` |
| `Data/DatabaseInitializer.cs` | Applies the schema script on first run |

**New infrastructure — `anamnys-aspire/keycloak/`**

| File | Responsibility |
|---|---|
| `Dockerfile` | Keycloak 26.6 + realm JSON + theme jar |
| `realms/anamnys-providers.json` | Providers realm, clients, roles, scopes, password policy, MFA |
| `realms/anamnys-patients.json` | Patients realm |
| `realms/anamnys-owners.json` | Owners realm |
| `theme/.gitignore` | Ignores the built jar |

**New frontend**

| Path | Responsibility |
|---|---|
| `apps/keycloak-theme/` | Keycloakify React login theme |
| `apps/admin/` | Owners SPA, `base: '/admin/'` |

**New tests — `anamnys-aspire.Tests/`**

| File | Responsibility |
|---|---|
| `Auth/SchemeIsolationTests.cs` | The 401-not-403 tests |
| `Auth/FirstLoginProvisionerTests.cs` | Provisioning idempotency |
| `Data/BreakGlassGrantConstraintTests.cs` | Two-person rule at the database level |

**Modified**

- `anamnys-aspire.AppHost/AppHost.cs` — Postgres, Keycloak, admin SPA
- `anamnys-aspire.AppHost/anamnys-aspire.AppHost.csproj` — hosting packages
- `anamnys-aspire.Server/anamnys-aspire.Server.csproj` — auth/EF packages, `RootNamespace`
- `anamnys-aspire.Server/Program.cs` — auth wiring, endpoint groups
- `anamnys-aspire.sln` — new projects
- `anamnys-db-script.sql` — schema amendments
- `../.gitignore` — un-ignore the schema script, ignore the theme jar
- `packages/shared/src/lib/types.ts`, `api/auth.ts`, `lib/store/authStore.ts` — redirect-based auth
- `apps/provider/src/routes/_auth/*` — deleted
- `CLAUDE.md` — new gotchas

---

## Task 1: Prove Keycloakify builds under this toolchain

The spec (§6, "Known risk") makes this a spike because Keycloakify 11.16.0 declares no `peerDependencies` and dev-builds against Vite 5 / React 18 / TypeScript 4.9, while this repo is on Vite 8 / React 19 / TypeScript 6.0.3. Nothing else in the plan is safe to build on until this is answered. **This task's deliverable is an answer plus a scaffold, not a finished theme.**

**Files:**
- Create: `apps/keycloak-theme/package.json`
- Create: `apps/keycloak-theme/vite.config.ts`
- Create: `apps/keycloak-theme/tsconfig.json`
- Create: `apps/keycloak-theme/index.html`
- Create: `apps/keycloak-theme/src/main.tsx`
- Create: `apps/keycloak-theme/src/kc.gen.ts` (generated — do not hand-write)
- Create: `keycloak/theme/.gitignore`
- Modify: `anamnys-aspire/package.json` (workspaces already glob `apps/*`; no edit expected — verify)

**Interfaces:**
- Consumes: nothing.
- Produces: `apps/keycloak-theme` builds a jar at `keycloak/theme/keycloak-theme-anamnys.jar` via `npm run build-keycloak-theme -w @anamnys/keycloak-theme`. Task 9 fills in the pages; Task 2's Dockerfile copies the jar.

- [ ] **Step 1: Scaffold the theme app**

Create `apps/keycloak-theme/package.json`:

```json
{
  "name": "@anamnys/keycloak-theme",
  "private": true,
  "version": "0.0.0",
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "npm run build-keycloak-theme",
    "build-keycloak-theme": "npm run build-vite && keycloakify build",
    "build-vite": "tsc -b && vite build",
    "lint": "eslint ."
  },
  "dependencies": {
    "@anamnys/shared": "*",
    "clsx": "^2.1.1",
    "i18next": "^26.3.6",
    "lucide-react": "^1.31.0",
    "react": "^19.2.1",
    "react-dom": "^19.2.1",
    "react-i18next": "^17.0.11",
    "tailwind-merge": "^3.6.0",
    "tailwindcss": "^4.3.3"
  },
  "devDependencies": {
    "@tailwindcss/vite": "^4.3.3",
    "@types/react": "^19.2.7",
    "@types/react-dom": "^19.2.3",
    "@vitejs/plugin-react": "^6.0.0",
    "keycloakify": "^11.16.0",
    "typescript": "~6.0.3",
    "vite": "^8.0.16"
  },
  "keycloakify": {
    "accountThemeImplementation": "none",
    "themeName": "anamnys",
    "keycloakVersionTargets": {
      "22-to-25": false,
      "all-other-versions": "keycloak-theme-anamnys.jar"
    }
  }
}
```

Create `apps/keycloak-theme/vite.config.ts`:

```ts
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { keycloakify } from 'keycloakify/vite-plugin';

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    keycloakify({
      // Emitted next to the Keycloak Dockerfile so the image build can COPY it
      // without reaching across the workspace.
      keycloakifyBuildDirPath: '../../keycloak/theme',
    }),
  ],
});
```

Create `apps/keycloak-theme/tsconfig.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "jsx": "react-jsx",
    "strict": true,
    "noEmit": true,
    "skipLibCheck": true,
    "allowImportingTsExtensions": true,
    "isolatedModules": true,
    "verbatimModuleSyntax": true
  },
  "include": ["src"]
}
```

Create `apps/keycloak-theme/index.html`:

```html
<!doctype html>
<html>
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Anamnys</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" src="/src/main.tsx"></script>
  </body>
</html>
```

Create `apps/keycloak-theme/src/main.tsx` — a deliberately minimal page whose only job is to prove the build pipeline works:

```tsx
import { createRoot } from 'react-dom/client';
import { StrictMode } from 'react';
import { KcPage } from './KcPage';

const rootEl = document.getElementById('root');
if (!rootEl) throw new Error('Missing #root');

createRoot(rootEl).render(
  <StrictMode>
    <KcPage />
  </StrictMode>,
);
```

Create `apps/keycloak-theme/src/KcPage.tsx`:

```tsx
export function KcPage() {
  return <div>Anamnys theme build probe</div>;
}
```

Create `keycloak/theme/.gitignore`:

```gitignore
# Keycloakify build output — produced by `npm run build-keycloak-theme`
*.jar
```

- [ ] **Step 2: Install and attempt the build**

Run:

```bash
npm install
npm run build-keycloak-theme -w @anamnys/keycloak-theme
```

Expected on success: `keycloak/theme/keycloak-theme-anamnys.jar` exists.

- [ ] **Step 3: Record the outcome and branch accordingly**

Check the result:

```bash
ls -la keycloak/theme/*.jar
```

**If the jar exists:** the toolchain is compatible. Delete nothing, change nothing, go to Step 4.

**If the build failed** with a Vite plugin API error (symptoms: `plugin.transform` signature errors, `configResolved` hook failures, or `keycloakify/vite-plugin` throwing on an unexpected config shape), pin this app's own toolchain — permitted for this app only, per Global Constraints. Edit `apps/keycloak-theme/package.json` `devDependencies`:

```json
    "typescript": "~5.6.3",
    "vite": "^5.4.11",
    "@vitejs/plugin-react": "^4.3.4"
```

Then re-run Step 2. npm workspaces installs a nested `node_modules` for the divergent versions; the three SPAs are unaffected because they resolve their own hoisted copies.

**If it still fails,** stop and report. Do not proceed to Task 9 — the theme approach itself needs revisiting, and that is a spec-level decision.

- [ ] **Step 4: Verify the three SPAs still build**

The workspace install just changed. Confirm nothing regressed:

```bash
npm run build -w @anamnys/provider
npm run lint -w @anamnys/provider
```

Expected: both succeed. If `npm run lint` fails on a TypeScript version peer error, the theme's pinned TypeScript hoisted over the SPAs' — move the pin into an `overrides` scoped to the theme app, or add `"typescript": "~6.0.3"` explicitly to each SPA's `devDependencies` (it is already there; verify it resolved).

- [ ] **Step 5: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/apps/keycloak-theme anamnys-aspire/keycloak/theme/.gitignore anamnys-aspire/package-lock.json
git commit -m "chore: scaffold keycloakify theme app and verify toolchain"
```

In the commit body, record which branch of Step 3 was taken and the exact versions used. Task 9 needs to know.

---

## Task 2: Postgres and Keycloak resources with the providers realm

**Files:**
- Modify: `anamnys-aspire.AppHost/anamnys-aspire.AppHost.csproj`
- Modify: `anamnys-aspire.AppHost/AppHost.cs`
- Create: `keycloak/Dockerfile`
- Create: `keycloak/realms/anamnys-providers.json`

**Interfaces:**
- Consumes: the theme jar path from Task 1.
- Produces: an Aspire resource named `keycloak` reachable at a fixed `http://localhost:8080`, serving realm `anamnys-providers` with clients `anamnys-web` and `anamnys-api`. Databases `keycloakdb` and `anamnysdb` on a resource named `postgres`. Tasks 6 and 7 consume the `keycloak` service name via `AddKeycloakOpenIdConnect`.

- [ ] **Step 1: Add hosting packages**

Edit `anamnys-aspire.AppHost/anamnys-aspire.AppHost.csproj`, adding to the existing `ItemGroup` that holds `Aspire.Hosting.Redis`:

```xml
    <PackageReference Include="Aspire.Hosting.Keycloak" Version="13.5.3-preview.1.26425.3" />
    <PackageReference Include="Aspire.Hosting.PostgreSQL" Version="13.5.3" />
    <PackageReference Include="CommunityToolkit.Aspire.Hosting.Keycloak.Extensions" Version="13.5.1-beta.736" />
```

`CommunityToolkit.Aspire.Hosting.Keycloak.Extensions` is a beta and that is deliberate: `Aspire.Hosting.Keycloak` 13.5.3 has no database method at all — its public surface is `AddKeycloak`, `WithRealmImport`, `WithDataVolume`, `WithArgs`, `WithContainerFiles`, and the standard container extensions. `WithPostgres` exists only in the toolkit package, which does ship a `net10.0` target. See the spec §7.

- [ ] **Step 2: Write the providers realm JSON**

Create `keycloak/realms/anamnys-providers.json`:

```json
{
  "realm": "anamnys-providers",
  "enabled": true,
  "displayName": "Anamnys",
  "loginTheme": "anamnys",
  "sslRequired": "external",
  "registrationAllowed": false,
  "resetPasswordAllowed": true,
  "verifyEmail": true,
  "loginWithEmailAllowed": true,
  "duplicateEmailsAllowed": false,
  "bruteForceProtected": true,
  "permanentLockout": false,
  "failureFactor": 5,
  "waitIncrementSeconds": 60,
  "maxFailureWaitSeconds": 900,
  "passwordPolicy": "length(12) and notUsername and notEmail and passwordHistory(5) and forceExpiredPasswordChange(0)",
  "otpPolicyType": "totp",
  "otpPolicyAlgorithm": "HmacSHA256",
  "otpPolicyDigits": 6,
  "otpPolicyPeriod": 30,
  "accessTokenLifespan": 300,
  "ssoSessionIdleTimeout": 1800,
  "ssoSessionMaxLifespan": 36000,
  "eventsEnabled": true,
  "eventsExpiration": 7776000,
  "adminEventsEnabled": true,
  "adminEventsDetailsEnabled": true,
  "roles": {
    "realm": [
      { "name": "provider", "description": "Licensed clinician" },
      { "name": "clinic-admin", "description": "Clinic administrator" },
      { "name": "billing-staff", "description": "Billing staff" }
    ]
  },
  "clientScopes": [
    {
      "name": "notes:read",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" }
    },
    {
      "name": "notes:write",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" }
    },
    {
      "name": "notes:sign",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" }
    },
    {
      "name": "billing:read",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" }
    },
    {
      "name": "patients:read",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" }
    }
  ],
  "clients": [
    {
      "clientId": "anamnys-api",
      "name": "Anamnys API",
      "enabled": true,
      "bearerOnly": true,
      "publicClient": false,
      "standardFlowEnabled": false,
      "serviceAccountsEnabled": false
    },
    {
      "clientId": "anamnys-web",
      "name": "Anamnys Provider BFF",
      "enabled": true,
      "publicClient": false,
      "secret": "${ANAMNYS_PROVIDER_CLIENT_SECRET}",
      "standardFlowEnabled": true,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": false,
      "redirectUris": ["http://localhost:5000/signin-oidc-provider", "https://localhost:5001/signin-oidc-provider"],
      "webOrigins": ["+"],
      "attributes": {
        "pkce.code.challenge.method": "S256",
        "post.logout.redirect.uris": "http://localhost:5000/provider/##https://localhost:5001/provider/"
      },
      "defaultClientScopes": ["openid", "profile", "email", "roles"],
      "optionalClientScopes": ["notes:read", "notes:write", "notes:sign", "billing:read", "patients:read"],
      "protocolMappers": [
        {
          "name": "audience-anamnys-api",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-audience-mapper",
          "config": {
            "included.client.audience": "anamnys-api",
            "access.token.claim": "true",
            "id.token.claim": "false"
          }
        }
      ]
    }
  ]
}
```

`directAccessGrantsEnabled: false` is not incidental — it is the setting that makes the Resource Owner Password Credentials flow the spec rejected structurally unavailable, not merely unused.

The redirect URIs use development ports. They are replaced by Aspire parameters in Task 3; hardcoding them now keeps this task independently verifiable.

- [ ] **Step 3: Write the Dockerfile**

Create `keycloak/Dockerfile`:

```dockerfile
FROM quay.io/keycloak/keycloak:26.6

COPY ./realms/*.json /opt/keycloak/data/import/
COPY ./theme/keycloak-theme-anamnys.jar /opt/keycloak/providers/
```

The theme ships as Keycloakify's jar into `/opt/keycloak/providers/`, not as an unpacked directory under `/opt/keycloak/themes/`. Keycloak's provider loading picks it up without a `--spi-theme` argument that would fight `AddKeycloak`'s defaults.

The `COPY` of the jar fails the image build if the jar is missing. That is intentional: a missing theme would otherwise produce a working Keycloak with the stock login page, which is a silent failure.

- [ ] **Step 4: Wire the resources in AppHost**

Rewrite `anamnys-aspire.AppHost/AppHost.cs`. The three existing SPA registrations and `PublishWithContainerFiles` calls stay exactly as they are; everything below is inserted above them.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

var providerClientSecret = builder.AddParameter("provider-client-secret", secret: true);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();

var keycloakDb = postgres.AddDatabase("keycloakdb");
var anamnysDb = postgres.AddDatabase("anamnysdb");

// Fixed port, not a dynamic one: cookies and redirect URIs are bound to the
// origin, so a port that moves between AppHost restarts invalidates every
// session and every registered redirect URI.
var keycloak = builder.AddKeycloak("keycloak", 8080)
    .WithDockerfile("./keycloak")
    .WithPostgres(keycloakDb)
    .WithDataVolume()
    .WithOtlpExporter()
    .WithEnvironment("ANAMNYS_PROVIDER_CLIENT_SECRET", providerClientSecret)
    .WaitFor(keycloakDb);

var server = builder.AddProject<Projects.anamnys_aspire_Server>("server")
    .WithReference(cache)
    .WithReference(anamnysDb)
    .WithReference(keycloak)
    .WaitFor(cache)
    .WaitFor(anamnysDb)
    .WaitFor(keycloak)
    .WithEnvironment("ANAMNYS_PROVIDER_CLIENT_SECRET", providerClientSecret)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();
```

Note `WithDockerfile("./keycloak")` is relative to the AppHost project directory, so the path resolves to `anamnys-aspire/anamnys-aspire.AppHost/../keycloak` only if the AppHost runs from the solution directory. Use `"../keycloak"` if Step 6 reports the Dockerfile was not found.

- [ ] **Step 5: Set the client secret parameter**

```bash
cd anamnys-aspire.AppHost
dotnet user-secrets set "Parameters:provider-client-secret" "$(openssl rand -hex 32)"
cd ..
```

User secrets, not `appsettings.json` — the secret must not reach git. The AppHost already has a `UserSecretsId`.

- [ ] **Step 6: Build the theme, then start and verify**

```bash
npm run build-keycloak-theme -w @anamnys/keycloak-theme
aspire stop
aspire start
```

Wait for the resources to report healthy, then verify the realm actually exists — the spec's §7 warning is that Keycloak's own health check passes whether or not the realm imported:

```bash
curl -s http://localhost:8080/realms/anamnys-providers/.well-known/openid-configuration | head -c 400
```

Expected: JSON containing `"issuer":"http://localhost:8080/realms/anamnys-providers"` and a `jwks_uri`. A 404 means the realm did not import — check the Keycloak container logs in the Aspire dashboard for the import step, and remember that with `WithDataVolume()` an existing volume prevents re-import.

Verify the theme loaded:

```bash
curl -s -o /dev/null -w '%{http_code}\n' "http://localhost:8080/realms/anamnys-providers/protocol/openid-connect/auth?client_id=anamnys-web&response_type=code&redirect_uri=http://localhost:5000/signin-oidc-provider"
```

Expected: `200`.

- [ ] **Step 7: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/keycloak anamnys-aspire/anamnys-aspire.AppHost
git commit -m "feat: add postgres and keycloak resources with providers realm"
```

---

## Task 3: Patients and owners realms, and parameterised redirect URIs

**Files:**
- Create: `keycloak/realms/anamnys-patients.json`
- Create: `keycloak/realms/anamnys-owners.json`
- Modify: `keycloak/realms/anamnys-providers.json`
- Modify: `anamnys-aspire.AppHost/AppHost.cs`

**Interfaces:**
- Consumes: the `keycloak` resource from Task 2.
- Produces: realms `anamnys-patients` (client `anamnys-patient-web`) and `anamnys-owners` (client `anamnys-admin-web`). Task 6 registers schemes against all three.

- [ ] **Step 1: Write the patients realm**

Create `keycloak/realms/anamnys-patients.json`. Same shape as the providers realm with these deliberate differences: `registrationAllowed` false (patients are invited by a provider, never self-registered), MFA available but not required, and a shorter session lifespan.

```json
{
  "realm": "anamnys-patients",
  "enabled": true,
  "displayName": "Anamnys",
  "loginTheme": "anamnys",
  "sslRequired": "external",
  "registrationAllowed": false,
  "resetPasswordAllowed": true,
  "verifyEmail": true,
  "loginWithEmailAllowed": true,
  "duplicateEmailsAllowed": false,
  "bruteForceProtected": true,
  "permanentLockout": false,
  "failureFactor": 5,
  "waitIncrementSeconds": 60,
  "maxFailureWaitSeconds": 900,
  "passwordPolicy": "length(10) and notUsername and notEmail",
  "otpPolicyType": "totp",
  "otpPolicyAlgorithm": "HmacSHA256",
  "otpPolicyDigits": 6,
  "otpPolicyPeriod": 30,
  "accessTokenLifespan": 300,
  "ssoSessionIdleTimeout": 900,
  "ssoSessionMaxLifespan": 7200,
  "eventsEnabled": true,
  "eventsExpiration": 7776000,
  "adminEventsEnabled": true,
  "adminEventsDetailsEnabled": true,
  "roles": {
    "realm": [{ "name": "patient", "description": "Patient" }]
  },
  "clientScopes": [
    {
      "name": "notes:read",
      "protocol": "openid-connect",
      "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" }
    }
  ],
  "clients": [
    {
      "clientId": "anamnys-api",
      "name": "Anamnys API",
      "enabled": true,
      "bearerOnly": true,
      "publicClient": false,
      "standardFlowEnabled": false,
      "serviceAccountsEnabled": false
    },
    {
      "clientId": "anamnys-patient-web",
      "name": "Anamnys Patient BFF",
      "enabled": true,
      "publicClient": false,
      "secret": "${ANAMNYS_PATIENT_CLIENT_SECRET}",
      "standardFlowEnabled": true,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": false,
      "redirectUris": ["${ANAMNYS_APP_ORIGIN}/signin-oidc-patient"],
      "webOrigins": ["+"],
      "attributes": {
        "pkce.code.challenge.method": "S256",
        "post.logout.redirect.uris": "${ANAMNYS_APP_ORIGIN}/patient/"
      },
      "defaultClientScopes": ["openid", "profile", "email", "roles"],
      "optionalClientScopes": ["notes:read"],
      "protocolMappers": [
        {
          "name": "audience-anamnys-api",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-audience-mapper",
          "config": {
            "included.client.audience": "anamnys-api",
            "access.token.claim": "true",
            "id.token.claim": "false"
          }
        }
      ]
    }
  ]
}
```

- [ ] **Step 2: Write the owners realm**

Create `keycloak/realms/anamnys-owners.json`. The scope vocabulary is disjoint from the PHI one — spec §2 — and MFA is required, matching the providers realm, because these accounts control the platform.

```json
{
  "realm": "anamnys-owners",
  "enabled": true,
  "displayName": "Anamnys Internal",
  "loginTheme": "anamnys",
  "sslRequired": "external",
  "registrationAllowed": false,
  "resetPasswordAllowed": true,
  "verifyEmail": true,
  "loginWithEmailAllowed": true,
  "duplicateEmailsAllowed": false,
  "bruteForceProtected": true,
  "permanentLockout": false,
  "failureFactor": 3,
  "waitIncrementSeconds": 120,
  "maxFailureWaitSeconds": 3600,
  "passwordPolicy": "length(16) and notUsername and notEmail and passwordHistory(10)",
  "otpPolicyType": "totp",
  "otpPolicyAlgorithm": "HmacSHA256",
  "otpPolicyDigits": 6,
  "otpPolicyPeriod": 30,
  "accessTokenLifespan": 300,
  "ssoSessionIdleTimeout": 900,
  "ssoSessionMaxLifespan": 14400,
  "eventsEnabled": true,
  "eventsExpiration": 7776000,
  "adminEventsEnabled": true,
  "adminEventsDetailsEnabled": true,
  "roles": {
    "realm": [
      { "name": "owner", "description": "Product owner" },
      { "name": "support", "description": "Support staff" },
      { "name": "ops", "description": "Operations staff" }
    ]
  },
  "clientScopes": [
    { "name": "tenancy:read", "protocol": "openid-connect", "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" } },
    { "name": "tenancy:write", "protocol": "openid-connect", "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" } },
    { "name": "billing:read", "protocol": "openid-connect", "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" } },
    { "name": "billing:write", "protocol": "openid-connect", "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" } },
    { "name": "plans:write", "protocol": "openid-connect", "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" } },
    { "name": "audit:read", "protocol": "openid-connect", "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" } },
    { "name": "ops:read", "protocol": "openid-connect", "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" } },
    { "name": "breakglass:request", "protocol": "openid-connect", "attributes": { "include.in.token.scope": "true", "display.on.consent.screen": "false" } }
  ],
  "clients": [
    {
      "clientId": "anamnys-api",
      "name": "Anamnys API",
      "enabled": true,
      "bearerOnly": true,
      "publicClient": false,
      "standardFlowEnabled": false,
      "serviceAccountsEnabled": false
    },
    {
      "clientId": "anamnys-admin-web",
      "name": "Anamnys Admin BFF",
      "enabled": true,
      "publicClient": false,
      "secret": "${ANAMNYS_OWNER_CLIENT_SECRET}",
      "standardFlowEnabled": true,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": false,
      "redirectUris": ["${ANAMNYS_APP_ORIGIN}/signin-oidc-owner"],
      "webOrigins": ["+"],
      "attributes": {
        "pkce.code.challenge.method": "S256",
        "post.logout.redirect.uris": "${ANAMNYS_APP_ORIGIN}/admin/"
      },
      "defaultClientScopes": ["openid", "profile", "email", "roles"],
      "optionalClientScopes": ["tenancy:read", "tenancy:write", "billing:read", "billing:write", "plans:write", "audit:read", "ops:read", "breakglass:request"],
      "protocolMappers": [
        {
          "name": "audience-anamnys-api",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-audience-mapper",
          "config": {
            "included.client.audience": "anamnys-api",
            "access.token.claim": "true",
            "id.token.claim": "false"
          }
        }
      ]
    }
  ]
}
```

Note there is **no PHI scope** in this realm. That is the whole point of §4 of the spec, and it must stay true: adding `notes:read` here would silently undo the design.

- [ ] **Step 3: Parameterise the providers realm's redirect URIs**

Edit `keycloak/realms/anamnys-providers.json`, replacing the hardcoded development URLs from Task 2:

```json
      "redirectUris": ["${ANAMNYS_APP_ORIGIN}/signin-oidc-provider"],
```

and

```json
        "post.logout.redirect.uris": "${ANAMNYS_APP_ORIGIN}/provider/"
```

- [ ] **Step 4: Feed the new parameters from AppHost**

In `anamnys-aspire.AppHost/AppHost.cs`, add two parameters next to the existing one:

```csharp
var patientClientSecret = builder.AddParameter("patient-client-secret", secret: true);
var ownerClientSecret = builder.AddParameter("owner-client-secret", secret: true);
```

and extend the keycloak resource's environment:

```csharp
    .WithEnvironment("ANAMNYS_PATIENT_CLIENT_SECRET", patientClientSecret)
    .WithEnvironment("ANAMNYS_OWNER_CLIENT_SECRET", ownerClientSecret)
    .WithEnvironment("ANAMNYS_APP_ORIGIN", server.GetEndpoint("http"))
```

`ANAMNYS_APP_ORIGIN` must reach the server too, since the OIDC handlers build their own redirect URIs against it. Add to the server resource:

```csharp
    .WithEnvironment("ANAMNYS_PATIENT_CLIENT_SECRET", patientClientSecret)
    .WithEnvironment("ANAMNYS_OWNER_CLIENT_SECRET", ownerClientSecret)
```

**Circular reference warning.** `keycloak` referencing `server.GetEndpoint("http")` while `server` has `.WaitFor(keycloak)` creates a cycle Aspire will reject at startup. Break it by declaring the server's endpoint explicitly rather than via `GetEndpoint` on a later-declared resource: move the `AddProject` call for `server` **above** the `AddKeycloak` call, keep `.WaitFor(keycloak)` off it, and instead add `keycloak` as a reference afterwards:

```csharp
var server = builder.AddProject<Projects.anamnys_aspire_Server>("server")
    .WithReference(cache)
    .WithReference(anamnysDb)
    .WaitFor(cache)
    .WaitFor(anamnysDb)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

var keycloak = builder.AddKeycloak("keycloak", 8080)
    .WithDockerfile("./keycloak")
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

- [ ] **Step 5: Set the two new secrets**

```bash
cd anamnys-aspire.AppHost
dotnet user-secrets set "Parameters:patient-client-secret" "$(openssl rand -hex 32)"
dotnet user-secrets set "Parameters:owner-client-secret" "$(openssl rand -hex 32)"
cd ..
```

- [ ] **Step 6: Recreate the volume and verify all three realms**

Realm import only runs when the realm does not already exist, and Task 2 created a data volume containing `anamnys-providers`. The two new realms will not appear until the volume is gone. This is the spec §7 gotcha, encountered for real:

```bash
aspire stop
docker volume ls --format '{{.Name}}' | grep -i keycloak
docker volume rm <the keycloak data volume from the line above>
aspire start
```

Then verify each realm:

```bash
for r in anamnys-providers anamnys-patients anamnys-owners; do
  printf '%s -> ' "$r"
  curl -s -o /dev/null -w '%{http_code}\n' "http://localhost:8080/realms/$r/.well-known/openid-configuration"
done
```

Expected: `200` three times.

Confirm the owners realm has no PHI scope:

```bash
curl -s http://localhost:8080/realms/anamnys-owners/.well-known/openid-configuration | grep -c 'notes:'
```

Expected: `0`.

- [ ] **Step 7: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/keycloak anamnys-aspire/anamnys-aspire.AppHost
git commit -m "feat: add patients and owners realms"
```

---

## Task 4: Schema amendments

**Files:**
- Modify: `../.gitignore`
- Modify: `anamnys-db-script.sql`

**Interfaces:**
- Consumes: nothing.
- Produces: `Providers.ExternalSubject uuid NOT NULL UNIQUE`, `PatientAccounts.ExternalSubject uuid UNIQUE NULL`, tables `Staff` and `BreakGlassGrants`. Task 5's entities map exactly these columns.

- [ ] **Step 1: Put the schema script under version control**

`anamnys-aspire/anamnys-db-script.sql` is currently gitignored, which contradicts the spec's premise that it is the schema of record. It contains no secrets — it is DDL for a greenfield database.

Edit `/Users/gersonjunior/Documents/git-repos/anamnys/.gitignore` and delete this line:

```gitignore
anamnys-aspire/anamnys-db-script.sql
```

Verify it is now trackable:

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git check-ignore -v anamnys-aspire/anamnys-db-script.sql
```

Expected: no output, exit code 1 (meaning "not ignored").

- [ ] **Step 2: Commit the script as-is, before amending it**

Commit the unamended file first so the amendments show up as a reviewable diff rather than arriving inside a 1072-line addition.

```bash
git add .gitignore anamnys-aspire/anamnys-db-script.sql
git commit -m "chore: track anamnys-db-script.sql as the schema of record"
```

- [ ] **Step 3: Amend the `Providers` table**

In `anamnys-db-script.sql`, find `CREATE TABLE "Providers"` (around line 558). Delete these three lines:

```sql
	"PasswordHash" text NOT NULL,
	"TwoFactorEnabled" boolean DEFAULT false NOT NULL,
	"TwoFactorSecretEncrypted" text,
```

and add, immediately after the `"Email"` line:

```sql
	"ExternalSubject" uuid NOT NULL CONSTRAINT "Providers_ExternalSubject_unique" UNIQUE,
```

Non-nullable because a `Providers` row is created *by* a successful first login — there is no window in which one exists without a Keycloak subject.

- [ ] **Step 4: Drop the `RecoveryCodes` table**

Delete the whole `CREATE TABLE "RecoveryCodes" (...)` block (around line 591) and its indexes near line 924:

```sql
CREATE UNIQUE INDEX "RecoveryCodes_pkey" ON "RecoveryCodes" ("Id");
```

Search for any other `RecoveryCodes` reference and remove it:

```bash
grep -n 'RecoveryCodes' anamnys-db-script.sql
```

Expected after editing: no output.

- [ ] **Step 5: Amend `PatientAccounts`**

Find `CREATE TABLE "PatientAccounts"` (around line 443). Delete:

```sql
	"AuthMethod" text DEFAULT 'magic_link' NOT NULL,
	"EmailVerifiedAt" timestamp with time zone,
	"FailedAttempts" integer DEFAULT 0 NOT NULL,
	"LockedUntil" timestamp with time zone,
```

and its check constraint:

```sql
	CONSTRAINT "PatientAccounts_AuthMethod_ck" CHECK (("AuthMethod" = ANY (ARRAY['magic_link'::text, 'otp'::text, 'password'::text])))
```

Add after the `"Email"` line:

```sql
	"ExternalSubject" uuid CONSTRAINT "PatientAccounts_ExternalSubject_unique" UNIQUE,
```

Nullable, unlike the provider column, because a provider creates a patient record long before that patient logs in — often before they are invited at all.

- [ ] **Step 6: Drop `PatientAuthTokens`**

Delete the whole `CREATE TABLE "PatientAuthTokens" (...)` block (around line 458) and its three indexes near line 896:

```sql
CREATE INDEX "PatientAuthTokens_ExpiresAt_idx" ON "PatientAuthTokens" ("ExpiresAt");
CREATE UNIQUE INDEX "PatientAuthTokens_pkey" ON "PatientAuthTokens" ("Id");
CREATE UNIQUE INDEX "PatientAuthTokens_TokenHash_key" ON "PatientAuthTokens" ("TokenHash");
```

Verify:

```bash
grep -n 'PatientAuthTokens' anamnys-db-script.sql
```

Expected: no output.

- [ ] **Step 7: Add the `Staff` and `BreakGlassGrants` tables**

Insert alphabetically — `Staff` after `SpecialistTitles`, `BreakGlassGrants` after `BookingPolicies` — to match the file's existing ordering:

```sql
CREATE TABLE "Staff" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"ExternalSubject" uuid NOT NULL CONSTRAINT "Staff_ExternalSubject_unique" UNIQUE,
	"Email" text NOT NULL CONSTRAINT "Staff_Email_unique" UNIQUE,
	"Name" text NOT NULL,
	"Role" text NOT NULL,
	"DisabledAt" timestamp with time zone,
	"CreatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"UpdatedAt" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "Staff_Role_ck" CHECK (("Role" = ANY (ARRAY['owner'::text, 'support'::text, 'ops'::text])))
);
```

```sql
CREATE TABLE "BreakGlassGrants" (
	"Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),
	"StaffId" uuid NOT NULL,
	"ProviderId" uuid NOT NULL,
	"TicketRef" text NOT NULL,
	"Reason" text NOT NULL,
	"AuthorizedBy" uuid NOT NULL,
	"AuthorizedAt" timestamp with time zone DEFAULT now() NOT NULL,
	"ExpiresAt" timestamp with time zone NOT NULL,
	"RevokedAt" timestamp with time zone,
	CONSTRAINT "BreakGlassGrants_TwoPerson_ck" CHECK (("AuthorizedBy" <> "StaffId")),
	CONSTRAINT "BreakGlassGrants_Window_ck" CHECK (("ExpiresAt" > "AuthorizedAt"))
);
```

The two-person rule is a database check constraint, not application logic, so an application bug cannot bypass it.

Add the indexes alongside the file's other `CREATE INDEX` statements:

```sql
CREATE INDEX "BreakGlassGrants_StaffId_idx" ON "BreakGlassGrants" ("StaffId");
CREATE INDEX "BreakGlassGrants_ProviderId_idx" ON "BreakGlassGrants" ("ProviderId");
CREATE INDEX "BreakGlassGrants_ExpiresAt_idx" ON "BreakGlassGrants" ("ExpiresAt");
```

- [ ] **Step 8: Verify the script still parses**

Run it against a throwaway Postgres container — a syntax error found here is far cheaper than one found at AppHost startup:

```bash
docker run --rm -d --name anamnys-schema-check -e POSTGRES_PASSWORD=x -p 55432:5432 postgres:18
until docker exec anamnys-schema-check pg_isready -U postgres >/dev/null 2>&1; do :; done
docker exec -i anamnys-schema-check psql -U postgres -v ON_ERROR_STOP=1 < anamnys-db-script.sql
echo "EXIT=$?"
docker rm -f anamnys-schema-check
```

Expected: `EXIT=0` and no `ERROR:` lines.

- [ ] **Step 9: Confirm no credential columns remain**

```bash
grep -niE '"(PasswordHash|TwoFactorEnabled|TwoFactorSecretEncrypted|AuthMethod|FailedAttempts|LockedUntil)"' anamnys-db-script.sql
```

Expected: exactly one line — `ShareLinks`'s `"PasswordHash"`, which stays. That is a password on a shared document link, not a user credential, and Keycloak has no opinion about it.

- [ ] **Step 10: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/anamnys-db-script.sql
git commit -m "refactor: remove application-owned credentials from schema

Keycloak owns passwords, TOTP secrets, recovery codes, email verification
and lockout. Adds ExternalSubject to Providers and PatientAccounts, and
the Staff and BreakGlassGrants tables for the owners realm."
```

---

## Task 5: EF Core context, entities, and the test project

**Files:**
- Modify: `anamnys-aspire.Server/anamnys-aspire.Server.csproj`
- Create: `anamnys-aspire.Server/Data/Entities/Provider.cs`
- Create: `anamnys-aspire.Server/Data/Entities/PatientAccount.cs`
- Create: `anamnys-aspire.Server/Data/Entities/Staff.cs`
- Create: `anamnys-aspire.Server/Data/Entities/BreakGlassGrant.cs`
- Create: `anamnys-aspire.Server/Data/Entities/AccessLog.cs`
- Create: `anamnys-aspire.Server/Data/AnamnysDbContext.cs`
- Create: `anamnys-aspire.Server/Data/DatabaseInitializer.cs`
- Create: `anamnys-aspire.Tests/anamnys-aspire.Tests.csproj`
- Create: `anamnys-aspire.Tests/Data/BreakGlassGrantConstraintTests.cs`
- Modify: `anamnys-aspire.Server/Program.cs`
- Modify: `anamnys-aspire.sln`

**Interfaces:**
- Consumes: the schema from Task 4, `anamnysdb` from Task 2.
- Produces:
  - `Anamnys.Server.Data.AnamnysDbContext` with `DbSet<Provider> Providers`, `DbSet<PatientAccount> PatientAccounts`, `DbSet<Staff> Staff`, `DbSet<BreakGlassGrant> BreakGlassGrants`, `DbSet<AccessLog> AccessLogs`.
  - `Provider` with `Guid Id`, `Guid ExternalSubject`, `string Email`, `string Name`, `string Specialty`, `string PreferredNoteFormat`, `DateTimeOffset CreatedAt`, `DateTimeOffset UpdatedAt`.
  - `Staff` with `Guid Id`, `Guid ExternalSubject`, `string Email`, `string Name`, `string Role`, `DateTimeOffset? DisabledAt`, `DateTimeOffset CreatedAt`, `DateTimeOffset UpdatedAt`.
  - `PatientAccount` with `Guid Id`, `Guid PatientId`, `string Email`, `Guid? ExternalSubject`.
  - Task 7's `FirstLoginProvisioner` consumes all of these.

**Why no EF migrations.** `anamnys-db-script.sql` is the schema of record (spec §5), and it describes 71 tables of which phase 1 models five. Generating migrations from a five-entity model would produce a database that contradicts the script. Instead `DatabaseInitializer` applies the script once, and the entities map onto it. `dotnet ef migrations` is not used in phase 1; revisit when the domain model covers the whole schema.

`WithInitFiles` on the Postgres resource was considered and rejected: files in `/docker-entrypoint-initdb.d` run against the server's default database before Aspire's `AddDatabase` creates `anamnysdb`, so the schema would land in the wrong database.

- [ ] **Step 1: Add packages and set the root namespace**

Edit `anamnys-aspire.Server/anamnys-aspire.Server.csproj`. Add to `PropertyGroup`:

```xml
    <RootNamespace>Anamnys.Server</RootNamespace>
```

The assembly name stays `anamnys-aspire.Server`; only the namespace changes, because `anamnys_aspire.Server` is not a name anyone should have to type. Existing `Program.cs` is top-level statements and `Extensions.cs` declares `namespace Microsoft.Extensions.Hosting;`, so neither is affected.

Add to the `ItemGroup`:

```xml
    <PackageReference Include="Aspire.Npgsql.EntityFrameworkCore.PostgreSQL" Version="13.5.3" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.11">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
```

Add an item group so the schema script travels with the assembly:

```xml
  <ItemGroup>
    <EmbeddedResource Include="..\anamnys-db-script.sql" LogicalName="anamnys-db-script.sql" />
  </ItemGroup>
```

- [ ] **Step 2: Write the entities**

Create `anamnys-aspire.Server/Data/Entities/Provider.cs`:

```csharp
namespace Anamnys.Server.Data.Entities;

public class Provider
{
    public Guid Id { get; set; }
    public Guid ExternalSubject { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Specialty { get; set; } = "MentalHealth";
    public string PreferredNoteFormat { get; set; } = "DAP";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

Create `anamnys-aspire.Server/Data/Entities/Staff.cs`:

```csharp
namespace Anamnys.Server.Data.Entities;

public class Staff
{
    public Guid Id { get; set; }
    public Guid ExternalSubject { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "support";
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

Create `anamnys-aspire.Server/Data/Entities/PatientAccount.cs`:

```csharp
namespace Anamnys.Server.Data.Entities;

public class PatientAccount
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public Guid? ExternalSubject { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

Create `anamnys-aspire.Server/Data/Entities/BreakGlassGrant.cs`:

```csharp
namespace Anamnys.Server.Data.Entities;

public class BreakGlassGrant
{
    public Guid Id { get; set; }
    public Guid StaffId { get; set; }
    public Guid ProviderId { get; set; }
    public string TicketRef { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid AuthorizedBy { get; set; }
    public DateTimeOffset AuthorizedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
```

Create `anamnys-aspire.Server/Data/Entities/AccessLog.cs`:

```csharp
namespace Anamnys.Server.Data.Entities;

public class AccessLog
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public string ActorType { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Scope { get; set; } = "";
    public string? TicketRef { get; set; }
    public DateTimeOffset? AuthorizedAt { get; set; }
    public DateTimeOffset At { get; set; }
}
```

- [ ] **Step 3: Write the DbContext**

Create `anamnys-aspire.Server/Data/AnamnysDbContext.cs`:

```csharp
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Data;

public class AnamnysDbContext(DbContextOptions<AnamnysDbContext> options) : DbContext(options)
{
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<PatientAccount> PatientAccounts => Set<PatientAccount>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<BreakGlassGrant> BreakGlassGrants => Set<BreakGlassGrant>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Provider>(e =>
        {
            e.ToTable("Providers");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalSubject).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<PatientAccount>(e =>
        {
            e.ToTable("PatientAccounts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalSubject).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<Staff>(e =>
        {
            e.ToTable("Staff");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExternalSubject).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<BreakGlassGrant>(e =>
        {
            e.ToTable("BreakGlassGrants");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StaffId);
            e.HasIndex(x => x.ProviderId);
        });

        modelBuilder.Entity<AccessLog>(e =>
        {
            e.ToTable("AccessLogs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ProviderId);
        });
    }
}
```

- [ ] **Step 4: Write the database initializer**

Create `anamnys-aspire.Server/Data/DatabaseInitializer.cs`:

```csharp
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Data;

public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnamnysDbContext>();

        var exists = await db.Database
            .SqlQuery<bool>($"SELECT to_regclass('public.\"Providers\"') IS NOT NULL AS \"Value\"")
            .SingleAsync(cancellationToken);

        if (exists)
        {
            logger.LogInformation("Schema already present; skipping bootstrap.");
            return;
        }

        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream("anamnys-db-script.sql")
            ?? throw new InvalidOperationException("Embedded resource anamnys-db-script.sql not found.");

        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);

        logger.LogInformation("Bootstrapping schema from anamnys-db-script.sql.");
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

The `to_regclass` probe is the idempotency guard: the script has no `IF NOT EXISTS` clauses, so running it twice would fail on the first `CREATE SCHEMA`.

- [ ] **Step 5: Register the context and initializer**

In `anamnys-aspire.Server/Program.cs`, after `builder.AddRedisClientBuilder("cache").WithOutputCache();`:

```csharp
builder.AddNpgsqlDbContext<AnamnysDbContext>("anamnysdb");
builder.Services.AddHostedService<DatabaseInitializer>();
```

and add `using Anamnys.Server.Data;` at the top of the file.

- [ ] **Step 6: Create the test project**

```bash
dotnet new xunit3 -o anamnys-aspire.Tests -n anamnys-aspire.Tests
dotnet sln anamnys-aspire.sln add anamnys-aspire.Tests/anamnys-aspire.Tests.csproj
dotnet add anamnys-aspire.Tests/anamnys-aspire.Tests.csproj reference anamnys-aspire.Server/anamnys-aspire.Server.csproj
dotnet add anamnys-aspire.Tests/anamnys-aspire.Tests.csproj package FluentAssertions --version 8.8.0
dotnet add anamnys-aspire.Tests/anamnys-aspire.Tests.csproj package Aspire.Hosting.Testing --version 13.5.3
dotnet add anamnys-aspire.Tests/anamnys-aspire.Tests.csproj reference anamnys-aspire.AppHost/anamnys-aspire.AppHost.csproj
```

Also add the AppHost's `IsAspireHost` marker so `DistributedApplicationTestingBuilder` can find it — add to `anamnys-aspire.Tests/anamnys-aspire.Tests.csproj`:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
  </ItemGroup>
```

- [ ] **Step 7: Write the failing constraint test**

Create `anamnys-aspire.Tests/Data/BreakGlassGrantConstraintTests.cs`:

```csharp
using Aspire.Hosting;
using FluentAssertions;
using Npgsql;

namespace Anamnys.Tests.Data;

public class BreakGlassGrantConstraintTests
{
    [Fact]
    public async Task Insert_WhenAuthorizedByEqualsStaffId_IsRejectedByDatabase()
    {
        // Arrange
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.anamnys_aspire_AppHost>(TestContext.Current.CancellationToken);
        await using var app = await appHost.BuildAsync(TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("server", TestContext.Current.CancellationToken);

        var connectionString = await app.GetConnectionStringAsync("anamnysdb", TestContext.Current.CancellationToken);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var sameId = Guid.NewGuid();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO "BreakGlassGrants"
                ("StaffId", "ProviderId", "TicketRef", "Reason", "AuthorizedBy", "ExpiresAt")
            VALUES (@id, @provider, 'TICKET-1', 'self-authorised', @id, now() + interval '1 hour')
            """, connection);
        command.Parameters.AddWithValue("id", sameId);
        command.Parameters.AddWithValue("provider", Guid.NewGuid());

        // Act
        var act = async () => await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // Assert
        var exception = await act.Should().ThrowAsync<PostgresException>();
        exception.Which.ConstraintName.Should().Be("BreakGlassGrants_TwoPerson_ck");
    }
}
```

Add the Npgsql package the test needs:

```bash
dotnet add anamnys-aspire.Tests/anamnys-aspire.Tests.csproj package Npgsql --version 10.0.3
```

- [ ] **Step 8: Run the test and watch it fail**

```bash
aspire stop
dotnet test anamnys-aspire.Tests/anamnys-aspire.Tests.csproj --filter "FullyQualifiedName~BreakGlassGrantConstraintTests"
```

Expected: FAIL. Before the entities and initializer are wired the `BreakGlassGrants` table does not exist, so the failure is `42P01: relation "BreakGlassGrants" does not exist` — not the constraint violation the test asserts.

- [ ] **Step 9: Run again after the initializer runs**

Steps 1–5 have already been written, so this run should pass. If it does not, read the failure: `42P01` still means the initializer did not run or the embedded resource name is wrong — check the server logs in the Aspire dashboard for "Bootstrapping schema".

```bash
dotnet test anamnys-aspire.Tests/anamnys-aspire.Tests.csproj --filter "FullyQualifiedName~BreakGlassGrantConstraintTests"
```

Expected: PASS.

- [ ] **Step 10: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/anamnys-aspire.Server anamnys-aspire/anamnys-aspire.Tests anamnys-aspire/anamnys-aspire.sln
git commit -m "feat: add EF Core context, auth entities and schema bootstrap"
```

---

## Task 6: Register the nine authentication schemes

**Files:**
- Modify: `anamnys-aspire.Server/anamnys-aspire.Server.csproj`
- Create: `anamnys-aspire.Server/Auth/AuthSchemes.cs`
- Create: `anamnys-aspire.Server/Auth/Realms.cs`
- Create: `anamnys-aspire.Server/Auth/RedisTicketStore.cs`
- Create: `anamnys-aspire.Server/Auth/TokenRefresher.cs`
- Create: `anamnys-aspire.Server/Auth/AuthenticationSetup.cs`
- Modify: `anamnys-aspire.Server/Program.cs`

**Interfaces:**
- Consumes: `keycloak` resource (Task 2/3), Redis `cache` (existing).
- Produces:
  - `Anamnys.Server.Auth.AuthSchemes` with `ProviderCookie`, `PatientCookie`, `OwnerCookie`, `ProviderOidc`, `PatientOidc`, `OwnerOidc`, `ProviderBearer`, `PatientBearer`, `OwnerBearer`, and the joined lists `ProviderSchemes`, `PatientSchemes`, `PhiSchemes`, `AdminSchemes`.
  - `Anamnys.Server.Auth.Realms` with `Providers`, `Patients`, `Owners`.
  - `builder.AddAnamnysAuthentication()` extension. Task 7 and Task 8 consume both.

- [ ] **Step 1: Add packages**

Edit `anamnys-aspire.Server/anamnys-aspire.Server.csproj`, adding to the `ItemGroup`:

```xml
    <PackageReference Include="Aspire.Keycloak.Authentication" Version="13.5.3-preview.1.26425.3" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" Version="10.0.11" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.11" />
    <PackageReference Include="Microsoft.AspNetCore.DataProtection.StackExchangeRedis" Version="10.0.11" />
```

- [ ] **Step 2: Write the constants**

Create `anamnys-aspire.Server/Auth/Realms.cs`:

```csharp
namespace Anamnys.Server.Auth;

public static class Realms
{
    public const string Providers = "anamnys-providers";
    public const string Patients = "anamnys-patients";
    public const string Owners = "anamnys-owners";
}
```

Create `anamnys-aspire.Server/Auth/AuthSchemes.cs`:

```csharp
namespace Anamnys.Server.Auth;

public static class AuthSchemes
{
    public const string ProviderCookie = "provider-cookie";
    public const string PatientCookie = "patient-cookie";
    public const string OwnerCookie = "owner-cookie";

    public const string ProviderOidc = "provider-oidc";
    public const string PatientOidc = "patient-oidc";
    public const string OwnerOidc = "owner-oidc";

    public const string ProviderBearer = "provider-bearer";
    public const string PatientBearer = "patient-bearer";
    public const string OwnerBearer = "owner-bearer";

    // For [Authorize(AuthenticationSchemes = ...)] on controllers or endpoint
    // filters, which take one comma-joined string. Route groups use
    // AddAuthenticationSchemes(params string[]) instead and pass the individual
    // constants above.
    //
    // A credential from a realm not in the list is not an authenticated
    // principal on that endpoint at all, so the request fails with 401 rather
    // than 403 — a missing scheme, not a failed policy.
    public const string ProviderSchemes = $"{ProviderCookie},{ProviderBearer}";
    public const string PatientSchemes = $"{PatientCookie},{PatientBearer}";
    public const string PhiSchemes = $"{ProviderCookie},{PatientCookie},{ProviderBearer},{PatientBearer}";
    public const string AdminSchemes = $"{OwnerCookie},{OwnerBearer}";
}
```

- [ ] **Step 3: Write the Redis ticket store**

Create `anamnys-aspire.Server/Auth/RedisTicketStore.cs`:

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using StackExchange.Redis;

namespace Anamnys.Server.Auth;

// Keeps access and refresh tokens server-side. The browser holds only the
// opaque key, so an XSS bug has nothing to exfiltrate and the cookie stays
// well clear of the 4KB limit once realm roles are in the token.
public sealed class RedisTicketStore(
    IConnectionMultiplexer redis,
    IDataProtectionProvider dataProtectionProvider) : ITicketStore
{
    private const string KeyPrefix = "auth:ticket:";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Anamnys.TicketStore");

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = KeyPrefix + Guid.NewGuid().ToString("N");
        await RenewAsync(key, ticket);
        return key;
    }

    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        var bytes = TicketSerializer.Default.Serialize(ticket);
        var protectedBytes = _protector.Protect(bytes);

        var expiresUtc = ticket.Properties.ExpiresUtc;
        var ttl = expiresUtc.HasValue
            ? expiresUtc.Value - DateTimeOffset.UtcNow
            : TimeSpan.FromHours(10);

        if (ttl <= TimeSpan.Zero)
        {
            ttl = TimeSpan.FromMinutes(1);
        }

        await redis.GetDatabase().StringSetAsync(key, protectedBytes, ttl);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        var value = await redis.GetDatabase().StringGetAsync(key);
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            var bytes = _protector.Unprotect((byte[])value!);
            return TicketSerializer.Default.Deserialize(bytes);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Key rotation or a tampered value. Treat as no session rather than
            // failing the request — the caller is challenged and logs in again.
            return null;
        }
    }

    public Task RemoveAsync(string key) => redis.GetDatabase().KeyDeleteAsync(key);
}
```

- [ ] **Step 4: Write the token refresher**

Create `anamnys-aspire.Server/Auth/TokenRefresher.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Anamnys.Server.Auth;

public sealed class TokenRefresher(
    IHttpClientFactory httpClientFactory,
    ILogger<TokenRefresher> logger)
{
    private sealed record TokenResponse(
        string access_token,
        string? refresh_token,
        int expires_in);

    public async Task ValidateAsync(
        CookieValidatePrincipalContext context,
        string realm,
        string clientId,
        string clientSecret)
    {
        var expiresAtRaw = context.Properties.GetTokenValue("expires_at");
        if (!DateTimeOffset.TryParse(expiresAtRaw, out var expiresAt))
        {
            return;
        }

        // Refresh a minute early so an in-flight request never races expiry.
        if (expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return;
        }

        var refreshToken = context.Properties.GetTokenValue("refresh_token");
        if (string.IsNullOrEmpty(refreshToken))
        {
            context.RejectPrincipal();
            return;
        }

        var client = httpClientFactory.CreateClient("keycloak");
        using var response = await client.PostAsync(
            $"realms/{realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            }),
            context.HttpContext.RequestAborted);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("Refresh failed for realm {Realm} with status {Status}.", realm, response.StatusCode);
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
            return;
        }

        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(context.HttpContext.RequestAborted);
        if (tokens is null)
        {
            context.RejectPrincipal();
            return;
        }

        context.Properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = tokens.access_token },
            new AuthenticationToken { Name = "refresh_token", Value = tokens.refresh_token ?? refreshToken },
            new AuthenticationToken
            {
                Name = "expires_at",
                Value = DateTimeOffset.UtcNow.AddSeconds(tokens.expires_in).ToString("o"),
            },
        ]);

        context.ShouldRenew = true;
    }
}
```

- [ ] **Step 5: Write the setup extension**

Create `anamnys-aspire.Server/Auth/AuthenticationSetup.cs`:

```csharp
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
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
        builder.Services.AddDataProtection()
            .PersistKeysToStackExchangeRedis(
                builder.Services.BuildServiceProvider().GetRequiredService<IConnectionMultiplexer>(),
                "anamnys:dataprotection-keys")
            .SetApplicationName("anamnys");

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

        builder.Services.AddHttpClient("keycloak", (serviceProvider, client) =>
        {
            client.BaseAddress = new Uri("http://keycloak/");
        });

        return builder;
    }
}
```

**Note on `BuildServiceProvider()` in the DataProtection call.** Building an intermediate provider during configuration is a known smell. If it causes a duplicate-multiplexer warning at startup, replace those lines with the configuration-based overload:

```csharp
        builder.Services.AddDataProtection()
            .PersistKeysToStackExchangeRedis(
                ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("cache")!),
                "anamnys:dataprotection-keys")
            .SetApplicationName("anamnys");
```

- [ ] **Step 6: Call it from Program.cs**

In `anamnys-aspire.Server/Program.cs`, after the DbContext registration from Task 5:

```csharp
builder.AddAnamnysAuthentication();
```

and in the pipeline, after `app.UseExceptionHandler();`:

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

Add `using Anamnys.Server.Auth;` at the top.

- [ ] **Step 7: Verify it starts**

```bash
aspire stop
dotnet build anamnys-aspire.sln
aspire start
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5000/health
```

Expected: build succeeds; health returns `200`. A startup crash naming a missing configuration key means Task 3's `WithEnvironment` calls on the `server` resource were not applied.

- [ ] **Step 8: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/anamnys-aspire.Server
git commit -m "feat: register cookie, OIDC and bearer schemes per realm"
```

---

## Task 7: Login, logout, provisioning, and `/api/auth/me`

**Files:**
- Create: `anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs`
- Create: `anamnys-aspire.Server/Auth/CurrentUser.cs`
- Create: `anamnys-aspire.Server/Auth/AuthEndpoints.cs`
- Modify: `anamnys-aspire.Server/Auth/AuthenticationSetup.cs`
- Modify: `anamnys-aspire.Server/Program.cs`
- Create: `anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs`
- Modify: `anamnys-aspire.Server/anamnys-aspire.Server.http`

**Interfaces:**
- Consumes: `AnamnysDbContext` (Task 5), `AuthSchemes`/`Realms` (Task 6).
- Produces:
  - `FirstLoginProvisioner.ProvisionAsync(ClaimsPrincipal principal, string realm, CancellationToken ct) → Task<Guid>` returning the local row id.
  - Claim type constant `AnamnysClaims.LocalId = "anamnys:lid"`.
  - `CurrentUser.LocalId(ClaimsPrincipal) → Guid` — the only sanctioned source of a provider id.
  - Endpoints `GET /auth/{provider|patient|owner}/login`, `POST /auth/{…}/logout`, `GET /api/auth/me`.

- [ ] **Step 1: Write the failing provisioning test**

Create `anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs`:

```csharp
using System.Security.Claims;
using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Tests.Auth;

public class FirstLoginProvisionerTests
{
    private static AnamnysDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AnamnysDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AnamnysDbContext(options);
    }

    private static ClaimsPrincipal PrincipalFor(Guid subject, string email, string name) =>
        new(new ClaimsIdentity(
        [
            new Claim("sub", subject.ToString()),
            new Claim("email", email),
            new Claim("name", name),
        ], "test"));

    [Fact]
    public async Task ProvisionAsync_WhenProviderIsUnknown_CreatesRowWithExternalSubject()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);
        var subject = Guid.NewGuid();

        // Act
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "clinician@example.com", "A Clinician"),
            Realms.Providers,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Providers.SingleAsync(TestContext.Current.CancellationToken);
        row.Id.Should().Be(localId);
        row.ExternalSubject.Should().Be(subject);
        row.Email.Should().Be("clinician@example.com");
    }

    [Fact]
    public async Task ProvisionAsync_WhenCalledTwiceForSameSubject_DoesNotCreateSecondRow()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);
        var subject = Guid.NewGuid();
        var principal = PrincipalFor(subject, "clinician@example.com", "A Clinician");

        // Act
        var first = await provisioner.ProvisionAsync(principal, Realms.Providers, TestContext.Current.CancellationToken);
        var second = await provisioner.ProvisionAsync(principal, Realms.Providers, TestContext.Current.CancellationToken);

        // Assert
        second.Should().Be(first);
        (await db.Providers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task ProvisionAsync_WhenPatientAccountDoesNotExist_ThrowsRatherThanCreating()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db);

        // Act
        var act = async () => await provisioner.ProvisionAsync(
            PrincipalFor(Guid.NewGuid(), "nobody@example.com", "Nobody"),
            Realms.Patients,
            TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
```

The third test encodes a real rule: a patient account is created by a provider inviting them, never by the patient logging in. `PatientAccounts.PatientId` is `NOT NULL` and references a `Patients` row that only a provider can create.

Add the in-memory provider the tests use:

```bash
dotnet add anamnys-aspire.Tests/anamnys-aspire.Tests.csproj package Microsoft.EntityFrameworkCore.InMemory --version 10.0.11
```

- [ ] **Step 2: Run the tests and watch them fail**

```bash
dotnet test anamnys-aspire.Tests/anamnys-aspire.Tests.csproj --filter "FullyQualifiedName~FirstLoginProvisionerTests"
```

Expected: FAIL to compile — `FirstLoginProvisioner` does not exist.

- [ ] **Step 3: Write the provisioner**

Create `anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs`:

```csharp
using System.Security.Claims;
using Anamnys.Server.Data;
using Anamnys.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Auth;

public static class AnamnysClaims
{
    public const string LocalId = "anamnys:lid";
}

public sealed class FirstLoginProvisioner(AnamnysDbContext db)
{
    public async Task<Guid> ProvisionAsync(
        ClaimsPrincipal principal,
        string realm,
        CancellationToken cancellationToken)
    {
        var subjectRaw = principal.FindFirstValue("sub")
            ?? throw new InvalidOperationException("Token has no sub claim.");
        var subject = Guid.Parse(subjectRaw);

        var email = principal.FindFirstValue("email")
            ?? throw new InvalidOperationException("Token has no email claim.");
        var name = principal.FindFirstValue("name") ?? email;

        return realm switch
        {
            Realms.Providers => await ProvisionProviderAsync(subject, email, name, cancellationToken),
            Realms.Owners => await ProvisionStaffAsync(subject, email, name, cancellationToken),
            Realms.Patients => await ResolvePatientAsync(subject, email, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown realm {realm}."),
        };
    }

    private async Task<Guid> ProvisionProviderAsync(Guid subject, string email, string name, CancellationToken cancellationToken)
    {
        var existing = await db.Providers
            .SingleOrDefaultAsync(p => p.ExternalSubject == subject, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var now = DateTimeOffset.UtcNow;
        var provider = new Provider
        {
            Id = Guid.NewGuid(),
            ExternalSubject = subject,
            Email = email,
            Name = name,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Providers.Add(provider);
        await db.SaveChangesAsync(cancellationToken);
        return provider.Id;
    }

    private async Task<Guid> ProvisionStaffAsync(Guid subject, string email, string name, CancellationToken cancellationToken)
    {
        var existing = await db.Staff
            .SingleOrDefaultAsync(s => s.ExternalSubject == subject, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var now = DateTimeOffset.UtcNow;
        var staff = new Staff
        {
            Id = Guid.NewGuid(),
            ExternalSubject = subject,
            Email = email,
            Name = name,
            Role = "support",
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Staff.Add(staff);
        await db.SaveChangesAsync(cancellationToken);
        return staff.Id;
    }

    // Patients are never created here. A PatientAccount exists only because a
    // provider created the Patient row and invited them, so a login with no
    // matching account is an error, not a signal to create one.
    private async Task<Guid> ResolvePatientAsync(Guid subject, string email, CancellationToken cancellationToken)
    {
        var bySubject = await db.PatientAccounts
            .SingleOrDefaultAsync(a => a.ExternalSubject == subject, cancellationToken);
        if (bySubject is not null)
        {
            return bySubject.Id;
        }

        var byEmail = await db.PatientAccounts
            .SingleOrDefaultAsync(a => a.Email == email && a.ExternalSubject == null, cancellationToken)
            ?? throw new InvalidOperationException("No patient account matches this login.");

        byEmail.ExternalSubject = subject;
        await db.SaveChangesAsync(cancellationToken);
        return byEmail.Id;
    }
}
```

- [ ] **Step 4: Run the tests and watch them pass**

```bash
dotnet test anamnys-aspire.Tests/anamnys-aspire.Tests.csproj --filter "FullyQualifiedName~FirstLoginProvisionerTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Call the provisioner from the OIDC handler**

In `anamnys-aspire.Server/Auth/AuthenticationSetup.cs`, register the provisioner alongside the other services:

```csharp
        builder.Services.AddScoped<FirstLoginProvisioner>();
```

and inside the `AddKeycloakOpenIdConnect` configuration lambda, add the event that stamps the local id onto the principal:

```csharp
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
```

Add `using System.Security.Claims;` to the file.

- [ ] **Step 6: Write `CurrentUser`**

Create `anamnys-aspire.Server/Auth/CurrentUser.cs`:

```csharp
using System.Security.Claims;

namespace Anamnys.Server.Auth;

// The only sanctioned source of a local row id. The id comes from the
// session, which came from a token this server obtained itself — never from
// anything the client supplied. Every provider-scoped query resolves through
// this.
public static class CurrentUser
{
    public static Guid LocalId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(AnamnysClaims.LocalId)
            ?? throw new InvalidOperationException("Principal has no local id claim.");
        return Guid.Parse(raw);
    }

    public static Guid? LocalIdOrNull(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(AnamnysClaims.LocalId);
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
```

- [ ] **Step 7: Write the endpoints**

Create `anamnys-aspire.Server/Auth/AuthEndpoints.cs`:

```csharp
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
            ClaimsPrincipal principal,
            AnamnysDbContext db,
            CancellationToken cancellationToken) =>
        {
            var localId = principal.LocalIdOrNull();
            if (localId is null)
            {
                return Results.Unauthorized();
            }

            var roles = principal.FindAll("roles").Select(c => c.Value).ToArray();
            var scheme = principal.Identity?.AuthenticationType ?? "";

            if (scheme.Contains("provider", StringComparison.OrdinalIgnoreCase))
            {
                var provider = await db.Providers
                    .SingleOrDefaultAsync(p => p.Id == localId, cancellationToken);
                return provider is null
                    ? Results.Unauthorized()
                    : Results.Ok(new MeResponse(provider.Id, provider.Email, provider.Name, Realms.Providers, roles));
            }

            if (scheme.Contains("owner", StringComparison.OrdinalIgnoreCase))
            {
                var staff = await db.Staff
                    .SingleOrDefaultAsync(s => s.Id == localId, cancellationToken);
                return staff is null
                    ? Results.Unauthorized()
                    : Results.Ok(new MeResponse(staff.Id, staff.Email, staff.Name, Realms.Owners, roles));
            }

            var account = await db.PatientAccounts
                .SingleOrDefaultAsync(a => a.Id == localId, cancellationToken);
            return account is null
                ? Results.Unauthorized()
                : Results.Ok(new MeResponse(account.Id, account.Email, account.Email, Realms.Patients, roles));
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
                new AuthenticationProperties { RedirectUri = returnUrl ?? appPath },
                [oidcScheme]))
            .AllowAnonymous();

        app.MapPost($"/auth/{segment}/logout", () =>
            Results.SignOut(
                new AuthenticationProperties { RedirectUri = appPath },
                [cookieScheme, oidcScheme]))
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(cookieScheme)
                .RequireAuthenticatedUser());
    }
}
```

**On `returnUrl`.** It is passed straight to `RedirectUri`, which ASP.NET Core treats as a local path. Verify in Step 9 that an absolute external URL is rejected; if it is not, add an `if (!Url.IsLocalUrl(returnUrl)) returnUrl = null;` guard — an open redirect on a login endpoint is a real phishing vector.

- [ ] **Step 8: Map the endpoints**

In `anamnys-aspire.Server/Program.cs`, before `app.MapDefaultEndpoints();`:

```csharp
app.MapAuthEndpoints();
```

- [ ] **Step 9: Verify the flow end to end in a browser**

```bash
aspire stop
aspire start
```

Create a test provider in Keycloak: open `http://localhost:8080`, sign in with the admin credentials shown in the Aspire dashboard for the `keycloak` resource, switch to realm `anamnys-providers`, and add a user with an email, a verified email flag, and a password.

Then visit `http://localhost:5000/auth/provider/login` in a browser.

Expected:
1. Redirect to Keycloak's login page (still the stock theme at this point — Task 9 replaces it).
2. After signing in, redirect back to `/provider/`.
3. `document.cookie` in the browser console shows **nothing** — the cookie is `HttpOnly`.
4. `fetch('/api/auth/me', { credentials: 'include' }).then(r => r.json()).then(console.log)` returns `{ id, email, name, realm: "anamnys-providers", roles: [...] }`.

Confirm the row was provisioned:

```bash
docker exec -i $(docker ps --filter name=postgres --format '{{.Names}}' | head -1) \
  psql -U postgres -d anamnysdb -c 'SELECT "Id", "Email", "ExternalSubject" FROM "Providers";'
```

Expected: exactly one row, `ExternalSubject` matching the Keycloak user's id.

Check the open-redirect guard:

```bash
curl -s -o /dev/null -w '%{redirect_url}\n' "http://localhost:5000/auth/provider/login?returnUrl=https://evil.example.com"
```

Expected: a redirect to Keycloak, and after login the browser must **not** land on `evil.example.com`. If it does, add the `Url.IsLocalUrl` guard from Step 7.

- [ ] **Step 10: Document the endpoints in the `.http` file**

`CLAUDE.md` requires every endpoint to be documented in `anamnys-aspire.Server/anamnys-aspire.Server.http` and exercised against a running AppHost. Create or append:

```http
@host = http://localhost:5000

### Begin provider login (open in a browser — this returns a 302 to Keycloak)
GET {{host}}/auth/provider/login

### Current session (requires the __Host-anamnys-provider cookie)
GET {{host}}/api/auth/me

### End the provider session
POST {{host}}/auth/provider/logout
```

- [ ] **Step 11: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/anamnys-aspire.Server anamnys-aspire/anamnys-aspire.Tests
git commit -m "feat: add login, logout, first-login provisioning and /api/auth/me"
```

---

## Task 8: Scheme-scoped endpoint groups and the isolation tests

This is the task that proves the design's central claim. Everything before it is plumbing.

**Files:**
- Modify: `anamnys-aspire.Server/Program.cs`
- Create: `anamnys-aspire.Tests/Auth/SchemeIsolationTests.cs`

**Interfaces:**
- Consumes: `AuthSchemes` (Task 6), the endpoints from Task 7.
- Produces: route groups `/api/phi` (PHI schemes only) and `/api/admin` (owner schemes only), each with a probe endpoint the tests use.

- [ ] **Step 1: Write the failing isolation tests**

Create `anamnys-aspire.Tests/Auth/SchemeIsolationTests.cs`:

```csharp
using System.Net;
using Aspire.Hosting;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

public class SchemeIsolationTests
{
    private static async Task<HttpClient> StartAppAsync(CancellationToken cancellationToken)
    {
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.anamnys_aspire_AppHost>(cancellationToken);
        var app = await appHost.BuildAsync(cancellationToken);
        await app.StartAsync(cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("server", cancellationToken);
        return app.CreateHttpClient("server");
    }

    [Fact]
    public async Task PhiEndpoint_WithNoCredential_Returns401()
    {
        // Arrange
        var client = await StartAppAsync(TestContext.Current.CancellationToken);

        // Act
        var response = await client.GetAsync("/api/phi/probe", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminEndpoint_WithNoCredential_Returns401()
    {
        // Arrange
        var client = await StartAppAsync(TestContext.Current.CancellationToken);

        // Act
        var response = await client.GetAsync("/api/admin/probe", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PhiEndpoint_WithOwnerRealmBearerToken_Returns401NotForbidden()
    {
        // Arrange — a genuine, valid token from the owners realm. It is not
        // forged and not expired; it is simply signed by the wrong realm.
        var client = await StartAppAsync(TestContext.Current.CancellationToken);
        var token = await KeycloakTestTokens.GetOwnerAccessTokenAsync(TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // Act
        var response = await client.GetAsync("/api/phi/probe", TestContext.Current.CancellationToken);

        // Assert — 401, never 403. A 403 would mean a registered scheme accepted
        // the issuer and only a policy stopped it, which is exactly the weaker
        // guarantee this design exists to avoid.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminEndpoint_WithProviderRealmBearerToken_Returns401NotForbidden()
    {
        // Arrange
        var client = await StartAppAsync(TestContext.Current.CancellationToken);
        var token = await KeycloakTestTokens.GetProviderAccessTokenAsync(TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // Act
        var response = await client.GetAsync("/api/admin/probe", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
```

- [ ] **Step 2: Write the token helper**

The realms disable `directAccessGrantsEnabled`, so the tests cannot ask Keycloak for a token with a password. Use a dedicated service-account client that exists only in test configuration.

Create `anamnys-aspire.Tests/Auth/KeycloakTestTokens.cs`:

```csharp
using System.Net.Http.Json;

namespace Anamnys.Tests.Auth;

internal static class KeycloakTestTokens
{
    private sealed record TokenResponse(string access_token);

    public static Task<string> GetProviderAccessTokenAsync(CancellationToken cancellationToken) =>
        GetAsync("anamnys-providers", "anamnys-test-provider", cancellationToken);

    public static Task<string> GetOwnerAccessTokenAsync(CancellationToken cancellationToken) =>
        GetAsync("anamnys-owners", "anamnys-test-owner", cancellationToken);

    private static async Task<string> GetAsync(string realm, string clientId, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://localhost:8080/") };
        using var response = await client.PostAsync(
            $"realms/{realm}/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = "test-only-not-a-secret",
            }),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        return payload!.access_token;
    }
}
```

Add the two service-account clients to the realm JSON. In `keycloak/realms/anamnys-providers.json`, append to `clients`:

```json
    {
      "clientId": "anamnys-test-provider",
      "name": "Integration test service account (development only)",
      "enabled": true,
      "publicClient": false,
      "secret": "test-only-not-a-secret",
      "standardFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": true,
      "protocolMappers": [
        {
          "name": "audience-anamnys-api",
          "protocol": "openid-connect",
          "protocolMapper": "oidc-audience-mapper",
          "config": {
            "included.client.audience": "anamnys-api",
            "access.token.claim": "true",
            "id.token.claim": "false"
          }
        }
      ]
    }
```

and the equivalent in `keycloak/realms/anamnys-owners.json` with `"clientId": "anamnys-test-owner"`.

**This client must never reach production.** Add a note in `CLAUDE.md` (Task 12) and, before the first deployment, gate it behind a separate `realms/dev/` overlay. Its secret is deliberately a literal, not a placeholder, so nobody mistakes it for a real credential.

- [ ] **Step 3: Run the tests and watch them fail**

```bash
aspire stop
dotnet test anamnys-aspire.Tests/anamnys-aspire.Tests.csproj --filter "FullyQualifiedName~SchemeIsolationTests"
```

Expected: FAIL with `404 NotFound` on all four — `/api/phi/probe` and `/api/admin/probe` do not exist yet. A 404 is the right failure here; it means the assertion is checking something real.

- [ ] **Step 4: Add the scheme-scoped groups**

In `anamnys-aspire.Server/Program.cs`, replace the existing weather-forecast block. The `api` group and its endpoint were template scaffolding; the two new groups are the real shape.

```csharp
// PHI lives behind provider and patient credentials only. The owners realm is
// absent from this list on purpose: an owner credential is not an
// authenticated principal here at all, so it fails with 401 rather than 403.
// See design/specs/2026-09-05-keycloak-implementation-design.md §2 and §4.
var phi = app.MapGroup("/api/phi")
    .RequireAuthorization(policy => policy
        .AddAuthenticationSchemes(
            AuthSchemes.ProviderCookie,
            AuthSchemes.PatientCookie,
            AuthSchemes.ProviderBearer,
            AuthSchemes.PatientBearer)
        .RequireAuthenticatedUser());

phi.MapGet("probe", (ClaimsPrincipal principal) => Results.Ok(new { localId = principal.LocalIdOrNull() }));

var admin = app.MapGroup("/api/admin")
    .RequireAuthorization(policy => policy
        .AddAuthenticationSchemes(AuthSchemes.OwnerCookie, AuthSchemes.OwnerBearer)
        .RequireAuthenticatedUser());

admin.MapGet("probe", (ClaimsPrincipal principal) => Results.Ok(new { localId = principal.LocalIdOrNull() }));
```

Add `using System.Security.Claims;` at the top of `Program.cs`.

Delete the `WeatherForecast` record and the `summaries` array — they are template leftovers and the `/api/weatherforecast` endpoint is now unreachable from any authenticated group.

- [ ] **Step 5: Recreate the Keycloak volume for the new test clients**

The realm JSON changed, and import only runs when the realm is absent:

```bash
aspire stop
docker volume ls --format '{{.Name}}' | grep -i keycloak
docker volume rm <the keycloak data volume>
```

- [ ] **Step 6: Run the tests and watch them pass**

```bash
dotnet test anamnys-aspire.Tests/anamnys-aspire.Tests.csproj --filter "FullyQualifiedName~SchemeIsolationTests"
```

Expected: PASS, 4 tests.

If `PhiEndpoint_WithOwnerRealmBearerToken_Returns401NotForbidden` returns **403** instead, the design has been violated: some scheme is accepting the owners issuer on a PHI endpoint. Check that the `phi` group's `AddAuthenticationSchemes` call lists no owner scheme, and that `AddAuthentication()` in `AuthenticationSetup` was not given a default scheme — a default scheme applies everywhere and would quietly undo the isolation. Do not "fix" the test.

- [ ] **Step 7: Run the whole suite**

```bash
dotnet test anamnys-aspire.sln
```

Expected: all tests pass — 3 provisioning, 1 constraint, 4 isolation.

- [ ] **Step 8: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/anamnys-aspire.Server anamnys-aspire/anamnys-aspire.Tests anamnys-aspire/keycloak
git commit -m "feat: scope endpoint groups by realm scheme, with isolation tests"
```

---

## Task 9: The React login theme

**Files:**
- Modify: `apps/keycloak-theme/src/KcPage.tsx`
- Create: `apps/keycloak-theme/src/login/Template.tsx`
- Create: `apps/keycloak-theme/src/login/pages/Login.tsx`
- Create: `apps/keycloak-theme/src/login/pages/LoginOtp.tsx`
- Create: `apps/keycloak-theme/src/login/pages/LoginResetPassword.tsx`
- Create: `apps/keycloak-theme/src/login/branding.ts`
- Create: `apps/keycloak-theme/src/index.css`
- Modify: `apps/keycloak-theme/src/main.tsx`

**Interfaces:**
- Consumes: `@anamnys/shared/ui/Button`, `@anamnys/shared/ui/TextField` (existing).
- Produces: a jar at `keycloak/theme/keycloak-theme-anamnys.jar` rendering the Anamnys design for `login.ftl`, `login-otp.ftl`, and `login-reset-password.ftl`.

The visual reference is the existing `apps/provider/src/routes/_auth.tsx` (the split hero/form shell) and `apps/provider/src/routes/_auth/login.tsx` (the form itself). Port the markup; do not redesign.

- [ ] **Step 1: Generate the Keycloakify page scaffolding**

```bash
npx keycloakify initialize-login-theme -p apps/keycloak-theme
npx keycloakify eject-page -p apps/keycloak-theme
```

The `eject-page` command is interactive: select `login`, then run it twice more selecting `login-otp` and `login-reset-password`. It writes the page components and updates `src/kc.gen.ts`.

Do not hand-edit `src/kc.gen.ts` — it is generated, and Keycloakify rewrites it.

- [ ] **Step 2: Write the per-realm branding map**

Create `apps/keycloak-theme/src/login/branding.ts`:

```ts
// One theme, three realms. The spec (§6) chose per-realm branding over three
// separate themes because the difference is a palette and a label, not a
// component tree.
type Branding = {
  logo: string;
  accent: string;
  productName: string;
};

const BRANDING: Record<string, Branding> = {
  'anamnys-providers': {
    logo: '/logo1.png',
    accent: 'var(--color-primary)',
    productName: 'Anamnys',
  },
  'anamnys-patients': {
    logo: '/logo1.png',
    accent: 'var(--color-primary)',
    productName: 'Anamnys',
  },
  'anamnys-owners': {
    logo: '/logo1.png',
    accent: 'var(--color-onSurfaceVariant)',
    productName: 'Anamnys Internal',
  },
};

const FALLBACK: Branding = BRANDING['anamnys-providers'];

export function brandingFor(realmName: string): Branding {
  return BRANDING[realmName] ?? FALLBACK;
}
```

- [ ] **Step 3: Write the shell template**

Create `apps/keycloak-theme/src/login/Template.tsx`, porting the layout from `apps/provider/src/routes/_auth.tsx`:

```tsx
import type { ReactNode } from 'react';
import { ShieldCheck } from 'lucide-react';
import { brandingFor } from './branding';
import type { KcContext } from '../kc.gen';

type Props = {
  kcContext: KcContext;
  headline: string;
  subhead: string;
  children: ReactNode;
};

export function Template({ kcContext, headline, subhead, children }: Props) {
  const branding = brandingFor(kcContext.realm.name);

  return (
    <div className="min-h-screen bg-surfaceContainerLow flex flex-col">
      <header className="bg-surface border-b border-surfaceVariant px-4">
        <div className="h-16 max-w-5xl mx-auto w-full flex items-center justify-between">
          <img
            src={branding.logo}
            alt={branding.productName}
            width={140}
            height={30}
            className="w-[140px] h-[30px] object-contain"
          />
        </div>
      </header>

      <div className="flex-1 flex items-start justify-center p-4 pt-10 md:pt-14 pb-8">
        <div className="w-full max-w-5xl flex flex-col md:flex-row md:items-start gap-8">
          <div className="hidden md:flex md:flex-[5] flex-col">
            <h1 className="text-headline-lg text-[30px] text-onSurface mb-2.5">{headline}</h1>
            <p className="text-body-lg text-onSurfaceVariant mb-6 max-w-[360px]">{subhead}</p>
            <div className="rounded-radii-lg overflow-hidden bg-surfaceContainerHigh shadow-xl relative flex-1 min-h-[240px]">
              <img src="/sign-up.jpg" alt="" className="absolute inset-0 w-full h-full object-cover" />
              <div className="absolute inset-x-0 bottom-0 h-[55%] bg-primary/30" />
              <div className="absolute left-4 right-4 bottom-4 flex items-center gap-2 bg-white/85 border border-white/50 rounded-radii-md px-3 py-2.5">
                <ShieldCheck size={16} className="text-primary" />
                <span className="text-label-md text-onSurface normal-case">
                  {kcContext.msg('loginTitleHtml', branding.productName)}
                </span>
              </div>
            </div>
          </div>

          <div className="flex-1 md:flex-[7]">
            <div className="bg-surfaceContainerLowest rounded-radii-xl border border-surfaceContainerHighest p-4 md:p-6 shadow-xl">
              {children}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
```

- [ ] **Step 4: Write the login page**

Create `apps/keycloak-theme/src/login/pages/Login.tsx`. This is a **native form post to Keycloak's `url.loginAction`**, not a controlled React form calling an API. Keycloak owns the credential exchange; the page only collects input.

```tsx
import { useState } from 'react';
import { Mail, Lock } from 'lucide-react';
import Button from '@anamnys/shared/ui/Button';
import TextField from '@anamnys/shared/ui/TextField';
import { Template } from '../Template';
import type { KcContext } from '../../kc.gen';

type Props = { kcContext: Extract<KcContext, { pageId: 'login.ftl' }> };

export default function Login({ kcContext }: Props) {
  const { url, realm, login, messagesPerField } = kcContext;
  const [submitting, setSubmitting] = useState(false);

  const usernameError = messagesPerField.existsError('username', 'password')
    ? messagesPerField.getFirstError('username', 'password')
    : null;

  return (
    <Template
      kcContext={kcContext}
      headline={kcContext.msg('loginAccountTitle')}
      subhead={kcContext.msg('doLogIn')}
    >
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">
        {kcContext.msg('loginAccountTitle')}
      </h2>

      {usernameError && (
        <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
          <p className="text-onErrorContainer text-[13px]">{usernameError}</p>
        </div>
      )}

      <form action={url.loginAction} method="post" onSubmit={() => setSubmitting(true)}>
        <TextField
          name="username"
          label={kcContext.msg('email')}
          icon={Mail}
          type="email"
          autoCapitalize="none"
          autoComplete="username"
          defaultValue={login.username ?? ''}
          containerClassName="mb-3.5"
        />
        <TextField
          name="password"
          label={kcContext.msg('password')}
          icon={Lock}
          secureToggle
          autoComplete="current-password"
          containerClassName="mb-3.5"
        />

        <Button
          type="submit"
          title={kcContext.msg('doLogIn')}
          loading={submitting}
          rounded="md"
          className="mt-1"
        />
      </form>

      {realm.resetPasswordAllowed && (
        <a href={url.loginResetCredentialsUrl} className="block text-center mt-4.5 text-label-lg text-primary">
          {kcContext.msg('doForgotPassword')}
        </a>
      )}
    </Template>
  );
}
```

`TextField` and `Button` must forward `name`, `type`, `defaultValue`, and `autoComplete` to the underlying elements. Check `packages/shared/src/ui/TextField.tsx` and `Button.tsx`; if they do not spread extra props onto the `<input>`/`<button>`, add `...rest` spreading in those two files as part of this task. A form post with no `name` attributes sends nothing and the login silently fails.

- [ ] **Step 5: Write the OTP and reset-password pages**

Create `apps/keycloak-theme/src/login/pages/LoginOtp.tsx`:

```tsx
import { useState } from 'react';
import { KeyRound } from 'lucide-react';
import Button from '@anamnys/shared/ui/Button';
import TextField from '@anamnys/shared/ui/TextField';
import { Template } from '../Template';
import type { KcContext } from '../../kc.gen';

type Props = { kcContext: Extract<KcContext, { pageId: 'login-otp.ftl' }> };

export default function LoginOtp({ kcContext }: Props) {
  const { url, messagesPerField } = kcContext;
  const [submitting, setSubmitting] = useState(false);
  const error = messagesPerField.existsError('totp') ? messagesPerField.get('totp') : null;

  return (
    <Template
      kcContext={kcContext}
      headline={kcContext.msg('doLogIn')}
      subhead={kcContext.msg('loginOtpOneTime')}
    >
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">
        {kcContext.msg('loginOtpOneTime')}
      </h2>

      {error && (
        <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
          <p className="text-onErrorContainer text-[13px]">{error}</p>
        </div>
      )}

      <form action={url.loginAction} method="post" onSubmit={() => setSubmitting(true)}>
        <TextField
          name="otp"
          label={kcContext.msg('loginOtpOneTime')}
          icon={KeyRound}
          autoCapitalize="none"
          autoComplete="one-time-code"
          inputMode="numeric"
          containerClassName="mb-3.5"
        />
        <Button type="submit" title={kcContext.msg('doLogIn')} loading={submitting} rounded="md" className="mt-1" />
      </form>
    </Template>
  );
}
```

Create `apps/keycloak-theme/src/login/pages/LoginResetPassword.tsx`:

```tsx
import { useState } from 'react';
import { Mail } from 'lucide-react';
import Button from '@anamnys/shared/ui/Button';
import TextField from '@anamnys/shared/ui/TextField';
import { Template } from '../Template';
import type { KcContext } from '../../kc.gen';

type Props = { kcContext: Extract<KcContext, { pageId: 'login-reset-password.ftl' }> };

export default function LoginResetPassword({ kcContext }: Props) {
  const { url, messagesPerField } = kcContext;
  const [submitting, setSubmitting] = useState(false);
  const error = messagesPerField.existsError('username') ? messagesPerField.get('username') : null;

  return (
    <Template
      kcContext={kcContext}
      headline={kcContext.msg('emailForgotTitle')}
      subhead={kcContext.msg('emailInstruction')}
    >
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">
        {kcContext.msg('emailForgotTitle')}
      </h2>
      <p className="text-body-md text-onSurfaceVariant mb-5">{kcContext.msg('emailInstruction')}</p>

      {error && (
        <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
          <p className="text-onErrorContainer text-[13px]">{error}</p>
        </div>
      )}

      <form action={url.loginAction} method="post" onSubmit={() => setSubmitting(true)}>
        <TextField
          name="username"
          label={kcContext.msg('email')}
          icon={Mail}
          type="email"
          autoCapitalize="none"
          autoComplete="username"
          containerClassName="mb-3.5"
        />
        <Button type="submit" title={kcContext.msg('doSubmit')} loading={submitting} rounded="md" className="mt-1" />
      </form>

      <a href={url.loginUrl} className="block text-center mt-4.5 text-label-lg text-primary">
        {kcContext.msg('backToLogin')}
      </a>
    </Template>
  );
}
```

- [ ] **Step 6: Route the pages**

Replace `apps/keycloak-theme/src/KcPage.tsx`:

```tsx
import { Suspense, lazy } from 'react';
import type { KcContext } from './kc.gen';
import './index.css';

const Login = lazy(() => import('./login/pages/Login'));
const LoginOtp = lazy(() => import('./login/pages/LoginOtp'));
const LoginResetPassword = lazy(() => import('./login/pages/LoginResetPassword'));
const DefaultPage = lazy(() => import('keycloakify/login/DefaultPage'));

export function KcPage({ kcContext }: { kcContext: KcContext }) {
  return (
    <Suspense>
      {(() => {
        switch (kcContext.pageId) {
          case 'login.ftl':
            return <Login kcContext={kcContext} />;
          case 'login-otp.ftl':
            return <LoginOtp kcContext={kcContext} />;
          case 'login-reset-password.ftl':
            return <LoginResetPassword kcContext={kcContext} />;
          default:
            return <DefaultPage kcContext={kcContext} />;
        }
      })()}
    </Suspense>
  );
}
```

Update `apps/keycloak-theme/src/main.tsx` to pass the context Keycloakify injects:

```tsx
import { createRoot } from 'react-dom/client';
import { StrictMode } from 'react';
import { KcPage } from './KcPage';
import { getKcContextMock } from './login/KcPageStory';

const kcContext = window.kcContext ?? getKcContextMock({ pageId: 'login.ftl' });

const rootEl = document.getElementById('root');
if (!rootEl) throw new Error('Missing #root');

createRoot(rootEl).render(
  <StrictMode>
    <KcPage kcContext={kcContext} />
  </StrictMode>,
);
```

If `initialize-login-theme` did not create `KcPageStory`, use Keycloakify's storybook helper name it did create — check `apps/keycloak-theme/src/login/` after Step 1 and adjust the import.

Create `apps/keycloak-theme/src/index.css`:

```css
@import "tailwindcss";
@source "../../../packages/shared/src";
```

The `@source` line is required: Tailwind 4 scans only the files it knows about, and the `Button`/`TextField` classes live outside this app.

- [ ] **Step 7: Preview the pages against a real Keycloak**

```bash
npm run build-keycloak-theme -w @anamnys/keycloak-theme
aspire stop
docker volume rm <the keycloak data volume>
aspire start
```

Visit `http://localhost:5000/auth/provider/login` and confirm the Anamnys design renders and a real login succeeds. Then visit `http://localhost:5000/auth/owner/login` and confirm the owners realm shows the "Anamnys Internal" branding.

Test the reset-password link and, after enrolling TOTP on the test user in the Keycloak admin console, the OTP page.

- [ ] **Step 8: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/apps/keycloak-theme anamnys-aspire/packages/shared
git commit -m "feat: build the Anamnys login theme with keycloakify"
```

---

## Task 10: Cut the provider SPA over to redirect-based auth

**Files:**
- Delete: `apps/provider/src/routes/_auth/login.tsx`
- Delete: `apps/provider/src/routes/_auth/register.tsx`
- Delete: `apps/provider/src/routes/_auth.tsx`
- Modify: `packages/shared/src/lib/types.ts`
- Modify: `packages/shared/src/api/auth.ts`
- Modify: `packages/shared/src/lib/store/authStore.ts`
- Modify: `apps/provider/src/routes/_app.tsx`
- Modify: `apps/provider/src/routeTree.gen.ts` (regenerated, must stay committed)

**Interfaces:**
- Consumes: `/auth/provider/login`, `/auth/provider/logout`, `/api/auth/me` (Task 7).
- Produces: `useAuthStore` with `{ user, isLoading, error, loadUser, login, logout, clearError }`. `AuthUser` becomes `{ id, email, name, realm, roles }`.

- [ ] **Step 1: Fix the `AuthUser` type**

In `packages/shared/src/lib/types.ts`, replace the auth types section. `AuthUser.token` must go — a token-shaped field in a type the browser holds is exactly what the BFF design forbids, and leaving it invites someone to populate it.

```ts
// ─── Auth types ────────────────────────────────────────────────────────────────

// Mirrors MeResponse from the server. There is deliberately no token field:
// tokens live in the server-side ticket store and never reach JavaScript.
export interface AuthUser {
  id: string;
  email: string;
  name: string;
  realm: string;
  roles: string[];
}
```

Delete `LoginRequest`, `RegisterRequest`, `LoginResponse`, `TwoFactorStatus`, `TwoFactorSetup`, and `TwoFactorVerifyResult` from this file — Keycloak owns all of those flows now.

- [ ] **Step 2: Reduce the auth API module**

Replace `packages/shared/src/api/auth.ts`:

```ts
import api from "@anamnys/shared/api/client";
import type { AuthUser } from "@anamnys/shared/lib/types";

// Login is not here on purpose. It is a full-page navigation to the BFF, which
// answers with a 302 to Keycloak — an XHR cannot follow that usefully.
export const authApi = {
  logout: async (realm: "provider" | "patient" | "owner"): Promise<void> => {
    await api.post(`/auth/${realm}/logout`).catch(() => undefined);
  },
  me: async (): Promise<AuthUser> => {
    const { data } = await api.get<AuthUser>("/auth/me");
    return data;
  },
};
```

Note the `logout` path: the axios instance has `baseURL: "/api"`, but `/auth/provider/logout` is **not** under `/api`. Fix by using an absolute path that bypasses the base URL:

```ts
    await api.post(`/auth/${realm}/logout`, undefined, { baseURL: "/" }).catch(() => undefined);
```

- [ ] **Step 3: Rewrite the auth store**

Replace `packages/shared/src/lib/store/authStore.ts`:

```ts
import { create } from "zustand";
import type { AuthUser } from "@anamnys/shared/lib/types";
import { authApi } from "@anamnys/shared/api/auth";

type Realm = "provider" | "patient" | "owner";

interface AuthState {
  user: AuthUser | null;
  isLoading: boolean;
  error: string | null;

  login: (realm: Realm, returnUrl?: string) => void;
  logout: (realm: Realm) => Promise<void>;
  loadUser: () => Promise<void>;
  clearError: () => void;
}

export const useAuthStore = create<AuthState>((set) => ({
  user: null,
  // Starts true: the (app) layout's auth gate checks `!isLoading && !user` in a
  // useEffect that fires before the ancestor effect calling loadUser(), because
  // child effects run first on initial mount. Defaulting to false would bounce a
  // genuinely authenticated user to login for one frame on every page load.
  isLoading: true,
  error: null,

  // A full-page navigation, not a fetch. The BFF answers with a 302 to Keycloak
  // and the whole document has to follow it.
  login: (realm, returnUrl) => {
    const query = returnUrl ? `?returnUrl=${encodeURIComponent(returnUrl)}` : "";
    window.location.assign(`/auth/${realm}/login${query}`);
  },

  logout: async (realm) => {
    await authApi.logout(realm);
    set({ user: null, error: null });
  },

  loadUser: async () => {
    set({ isLoading: true });
    try {
      const user = await authApi.me();
      set({ user, isLoading: false });
    } catch {
      set({ user: null, isLoading: false });
    }
  },

  clearError: () => set({ error: null }),
}));
```

- [ ] **Step 4: Delete the SPA auth routes**

```bash
rm apps/provider/src/routes/_auth/login.tsx
rm apps/provider/src/routes/_auth/register.tsx
rm apps/provider/src/routes/_auth.tsx
rmdir apps/provider/src/routes/_auth
```

- [ ] **Step 5: Point the app gate at the BFF**

Replace the redirect in `apps/provider/src/routes/_app.tsx`:

```tsx
import { useEffect } from "react";
import { createFileRoute, Outlet } from "@tanstack/react-router";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import TopBar from "@anamnys/shared/ui/TopBar";
import AppTabBar from "@anamnys/shared/ui/AppTabBar";
import Sidenav from "@anamnys/shared/ui/Sidenav";

export const Route = createFileRoute("/_app")({
  component: AppLayout,
});

function AppLayout() {
  const { user, isLoading, login } = useAuthStore();

  useEffect(() => {
    // Not navigate({ to: "/login" }) — there is no login route in this app
    // any more. The BFF redirects to Keycloak, which renders the theme.
    if (!isLoading && !user) login("provider", window.location.pathname);
  }, [isLoading, user, login]);

  if (isLoading || !user) return null;

  return (
    <div className="min-h-screen flex flex-col bg-surfaceContainerLow">
      <Sidenav />
      <div className="flex flex-col flex-1 md:ml-72">
        <TopBar />
        <main className="flex-1">
          <Outlet />
        </main>
        <AppTabBar />
      </div>
    </div>
  );
}
```

- [ ] **Step 6: Find and fix every remaining reference**

```bash
grep -rn "pendingTwoFactor\|completeTwoFactorLogin\|cancelTwoFactor\|isSubmitting\|LoginResponse\|RegisterRequest\|LoginRequest\|authApi.login\|authApi.register\|\.token" apps packages --include=*.ts --include=*.tsx
```

Fix each hit. `AccountMenu.tsx` and `settings/account.tsx` are the likely callers of `logout` and the 2FA APIs; the 2FA settings UI must be replaced with a link to Keycloak's account console at `http://localhost:8080/realms/anamnys-providers/account/`, since Keycloak owns TOTP enrolment now.

- [ ] **Step 7: Regenerate the route tree and build**

```bash
npx vite build --config apps/provider/vite.config.ts
npm run build -w @anamnys/provider
npm run lint -w @anamnys/provider
```

Expected: all succeed. `apps/provider/src/routeTree.gen.ts` will have changed — it is generated but **must stay committed**, because `npm run build` runs `tsc -b` before Vite regenerates it and a clean checkout fails without it.

- [ ] **Step 8: Verify in the browser**

```bash
aspire stop && aspire start
```

Visit `http://localhost:5000/provider/` while logged out. Expected: an automatic redirect to the Keycloak-hosted Anamnys login page, and after signing in, a return to `/provider/` with the app rendered.

- [ ] **Step 9: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/apps/provider anamnys-aspire/packages/shared
git commit -m "refactor: move provider login to the keycloak-hosted theme"
```

---

## Task 11: Scaffold the admin SPA

**Files:**
- Create: `apps/admin/` (package.json, vite.config.ts, tsconfig*.json, eslint.config.js, index.html, admin.esproj, src/main.tsx, src/index.css, src/routes/__root.tsx, src/routes/index.tsx, src/routes/_app.tsx)
- Modify: `anamnys-aspire.AppHost/AppHost.cs`
- Modify: `anamnys-aspire.sln`

**Interfaces:**
- Consumes: `useAuthStore` (Task 10), `/auth/owner/login` (Task 7).
- Produces: an Aspire resource named `admin` published to `wwwroot/admin`.

- [ ] **Step 1: Copy the patient app as the starting point**

`apps/patient` is the smallest of the three and has the same shape.

```bash
cp -R apps/patient apps/admin
rm -rf apps/admin/node_modules apps/admin/dist apps/admin/obj*
mv apps/admin/patient.esproj apps/admin/admin.esproj
```

- [ ] **Step 2: Rename the package**

Edit `apps/admin/package.json`, changing only the name:

```json
  "name": "@anamnys/admin",
```

- [ ] **Step 3: Set the base path**

Edit `apps/admin/vite.config.ts`, changing the `base` option:

```ts
  base: '/admin/',
```

Everything else — the `/api` and `/hubs` proxy targets, the TanStack Router plugin ordering — stays as it is. The sub-path must match the `PublishWithContainerFiles` target in Step 5 or the built assets 404.

- [ ] **Step 4: Write a gated landing route**

Replace `apps/admin/src/routes/index.tsx`:

```tsx
import { useEffect } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";

export const Route = createFileRoute("/")({
  component: AdminHome,
});

function AdminHome() {
  const { user, isLoading, login, loadUser } = useAuthStore();

  useEffect(() => {
    void loadUser();
  }, [loadUser]);

  useEffect(() => {
    if (!isLoading && !user) login("owner", window.location.pathname);
  }, [isLoading, user, login]);

  if (isLoading || !user) return null;

  return (
    <div className="min-h-screen bg-surfaceContainerLow p-8">
      <h1 className="text-headline-lg text-onSurface mb-2">Anamnys Internal</h1>
      <p className="text-body-lg text-onSurfaceVariant">
        Signed in as {user.email} ({user.roles.join(", ") || "no roles"}).
      </p>
    </div>
  );
}
```

This app has no features yet — spec §11 puts them out of scope. Its job in phase 1 is to prove the owners realm authenticates end to end.

- [ ] **Step 5: Register it in AppHost**

In `anamnys-aspire.AppHost/AppHost.cs`, alongside the other three:

```csharp
var admin = builder.AddViteApp("admin", "../apps/admin")
    .WithReference(server)
    .WaitFor(server);
```

and next to the other publish calls:

```csharp
server.PublishWithContainerFiles(admin, "wwwroot/admin");
```

- [ ] **Step 6: Add to the solution**

```bash
dotnet sln anamnys-aspire.sln add apps/admin/admin.esproj
```

- [ ] **Step 7: Install, build, and verify**

```bash
npm install
npm run build -w @anamnys/admin
npm run lint -w @anamnys/admin
aspire stop && aspire start
```

Create an owners-realm test user in the Keycloak admin console (realm `anamnys-owners`), then visit `http://localhost:5000/admin/`.

Expected: redirect to the login page with "Anamnys Internal" branding, then the landing page showing the signed-in email.

Confirm the `Staff` row was provisioned:

```bash
docker exec -i $(docker ps --filter name=postgres --format '{{.Names}}' | head -1) \
  psql -U postgres -d anamnysdb -c 'SELECT "Email", "Role", "ExternalSubject" FROM "Staff";'
```

Expected: one row with `Role = 'support'`.

Confirm the owner cannot reach PHI, in the browser console on `/admin/`:

```js
fetch('/api/phi/probe', { credentials: 'include' }).then(r => console.log(r.status));
```

Expected: `401`. If it logs `403`, stop — the design is broken; see Task 8 Step 6.

- [ ] **Step 8: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/apps/admin anamnys-aspire/anamnys-aspire.AppHost anamnys-aspire/anamnys-aspire.sln anamnys-aspire/package-lock.json
git commit -m "feat: scaffold the admin SPA on the owners realm"
```

---

## Task 12: Documentation and final verification

**Files:**
- Modify: `CLAUDE.md`
- Modify: `../.gitignore`

**Interfaces:**
- Consumes: everything.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Update the tech stack section**

In `CLAUDE.md`, move Keycloak, EF Core, and PostgreSQL from "Backend — planned" to "Backend — present", and add Keycloakify to the frontend section. Delete the line describing `frontend/` as a single Vite SPA if it still says that — there are now four apps plus a theme.

- [ ] **Step 2: Add the new gotchas**

Append to the `## Gotchas` section:

```markdown
- **Realm import only runs when the realm does not exist.** `keycloak` uses
  `WithDataVolume()`, so editing `keycloak/realms/*.json` changes nothing until the
  volume is deleted: `aspire stop`, `docker volume rm <keycloak volume>`, `aspire start`.
  The failure is silent — Keycloak starts healthy with the *old* realm.
- **The Keycloak image needs the theme jar to exist before it builds.**
  `npm run build-keycloak-theme -w @anamnys/keycloak-theme` writes
  `keycloak/theme/keycloak-theme-anamnys.jar`, which the Dockerfile `COPY`s. The jar is
  gitignored. A missing jar fails the image build, which is deliberate: the alternative
  is a Keycloak serving the stock login page and nobody noticing.
- **Cookies are distinguished by name, not path.** `__Host-anamnys-provider`,
  `-patient`, and `-owner` all live at `Path=/`. Scoping them to `/provider/` etc. looks
  tidier and breaks everything, because API calls go to `/api/*` and the cookie would
  not be sent.
- **A wrong-realm credential must produce 401, not 403.** Endpoint groups list their
  accepted authentication schemes; an unlisted realm is not an authenticated principal
  there at all. If a test starts returning 403, a scheme has leaked into a group it
  should not be in — fix the group, never the test.
- **`anamnys-test-provider` and `anamnys-test-owner` are development-only clients** with
  a literal secret, used by the integration tests because the real clients disable the
  direct-access grant. They must not reach production; gate them behind a `realms/dev/`
  overlay before the first deployment.
- **Keycloak owns TOTP enrolment, password reset, and recovery codes.** There is no 2FA
  settings UI in the SPA — link to the Keycloak account console instead.
```

- [ ] **Step 3: Update the API endpoint workflow note**

The `### Creating/Modifying API Endpoints` section says to add endpoints to the `.http` file. Add a line about scheme selection:

```markdown
6. Choose the endpoint group deliberately: `/api/phi/*` for anything touching patient
   data, `/api/admin/*` for owners-realm surfaces. The group determines which realms can
   authenticate at all — see `design/specs/2026-09-05-keycloak-implementation-design.md` §2.
```

- [ ] **Step 4: Ignore the theme build output at the root level**

Confirm `keycloak/theme/.gitignore` from Task 1 is doing its job:

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
npm --prefix anamnys-aspire run build-keycloak-theme -w @anamnys/keycloak-theme
git status --porcelain anamnys-aspire/keycloak/theme
```

Expected: no output — the jar is ignored.

- [ ] **Step 5: Full verification against the spec's definition of done**

Spec §10 lists seven criteria. Check each:

```bash
cd anamnys-aspire
aspire stop
dotnet build anamnys-aspire.sln
npm run build
npm run lint
npm run build-keycloak-theme -w @anamnys/keycloak-theme
dotnet test anamnys-aspire.sln
aspire start
```

Then, in a browser:

1. A provider signs in through the React login page. ✓ when `/provider/` renders after login.
2. The BFF holds the tokens. ✓ when `document.cookie` is empty on `/provider/` and `redis-cli KEYS 'auth:ticket:*'` returns entries.
3. A `Providers` row exists with `ExternalSubject` populated. ✓ via the psql query from Task 7.
4. `/api/auth/me` returns it. ✓ via fetch.
5. Logout clears the session and signs out at Keycloak. ✓ when after logout, visiting `/provider/` redirects to the login page rather than resuming.
6. An owners cookie gets 401, not 403, from a provider endpoint. ✓ via the Task 11 Step 7 console check and `SchemeIsolationTests`.
7. `aspire publish` produces a Keycloak image with realms and theme baked in:

```bash
aspire stop
aspire publish
```

Expected: succeeds, and the generated manifest references a built Keycloak image rather than `quay.io/keycloak/keycloak` directly. Confirm `WithRealmImport` appears nowhere in `AppHost.cs`:

```bash
grep -n 'WithRealmImport' anamnys-aspire.AppHost/AppHost.cs
```

Expected: no output. `WithRealmImport` is development-only and silently dropped by `aspire publish` — the whole reason for the custom image.

- [ ] **Step 6: Commit**

```bash
cd /Users/gersonjunior/Documents/git-repos/anamnys
git add anamnys-aspire/CLAUDE.md .gitignore
git commit -m "docs: record keycloak gotchas and update the tech stack"
```

- [ ] **Step 7: Open the pull request**

```bash
git push -u origin feature/keycloak-auth
gh pr create --base develop --title "feat: implement Keycloak authentication" --body "$(cat <<'EOF'
Implements `design/specs/2026-09-05-keycloak-implementation-design.md`, phase 1.

## What this adds

- Three Keycloak realms — providers, patients, owners — seeded from committed JSON
  baked into a custom image, so development and production seed identically.
- A React login page built with Keycloakify and compiled into that image. The
  Authorization Code + PKCE redirect is unchanged; only the rendered page is ours.
- A Backend-for-Frontend: three cookie schemes distinguished by name, three OIDC
  handlers, three bearer schemes. Access and refresh tokens live in a Redis-backed
  ticket store; the browser holds only an opaque session id.
- EF Core with `ExternalSubject` on `Providers` and `PatientAccounts`, plus `Staff`
  and `BreakGlassGrants`, and first-login provisioning.
- An `apps/admin` SPA on the owners realm.

## The load-bearing test

`SchemeIsolationTests` asserts that a valid owners-realm token gets **401** from a PHI
endpoint, not 403. A 403 would mean a registered scheme accepted the issuer and only a
policy stopped it — the weaker guarantee this design exists to avoid.

## Schema changes

`anamnys-db-script.sql` is now tracked (it was gitignored) and no longer carries
application-owned credentials: `PasswordHash`, `TwoFactorEnabled`,
`TwoFactorSecretEncrypted`, `RecoveryCodes`, `PatientAuthTokens`, and the
`PatientAccounts` lockout columns are gone. Keycloak owns all of it.

## Out of scope

Break-glass endpoints, admin features, MFA enforcement, and audit event export —
spec §11 and the 2026-08-24 design's phase 4.

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_013XPfJJNgesK2wiwVDpPLiC
EOF
)"
```

---

## Notes for the executor

**Where this plan is uncertain, and what to do about it.**

1. **Task 1 is a genuine spike.** Keycloakify's compatibility with Vite 8 is unverified — the package declares no peer dependencies, so npm will not warn you either way. If Step 3's fallback also fails, stop and escalate; the theme approach is a spec-level decision, not something to work around.
2. **Task 3 Step 4 describes a circular-reference hazard** between `server` and `keycloak`. The ordering given avoids it. If Aspire still reports a cycle, replace `server.GetEndpoint("http")` with a plain string parameter for the app origin and set it from configuration.
3. **`AuthenticationSetup` calls `BuildServiceProvider()`** during configuration, which is a smell. Step 5 gives the replacement if it misbehaves.
4. **`TextField` and `Button` may not forward `name`/`type`/`defaultValue`.** Task 9 Step 4 says to check and fix. A Keycloak form post with no `name` attributes submits nothing and fails silently — verify with an actual login, not by reading the code.
5. **Do not weaken a failing isolation test.** If Task 8 or Task 11 produces a 403 where the plan says 401, the endpoint grouping is wrong. That test is the design.

**One deliberate deviation from the spec.** Spec §4 says phase 1 ships "the tables, the grant lifecycle, and the `AccessLogs` write path". This plan ships the tables (Task 4), the entities (Task 5), and the two-person constraint test (Task 5) — but not a grant-issuing service or the `AccessLogs` write. Both exist only to serve the break-glass endpoints, which the same section defers until there is PHI to reach, so building them now would mean writing an API with no caller and no test that exercises it for real. The constraint that actually matters — `AuthorizedBy <> StaffId` — is enforced in the database from day one and is covered by a test. If you would rather hold to the spec's letter, add a `BreakGlassService.IssueAsync(...)` and an `AccessLogWriter` in Task 5; the tables are already shaped for them.
