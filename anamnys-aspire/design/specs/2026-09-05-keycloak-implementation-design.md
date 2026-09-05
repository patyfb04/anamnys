# Keycloak Implementation Design

**Date:** 2026-09-05
**Status:** Approved (design); implementation not started
**Supersedes nothing.** Extends `2026-08-24-authentication-design.md`, which remains the
authority on *why* Keycloak was chosen. This document is the *how*, plus the three
decisions that document did not cover: a third realm, a custom React login page, and the
concrete BFF session mechanics.

---

## 1. What changed since the 2026-08-24 design

Three additions, each approved 2026-09-05:

1. **A third realm, `anamnys-owners`**, for the people who own the product. Full reach over
   the business — tenancy, billing, plans, provider accounts, audit, ops — and no standing
   access to PHI.
2. **The login page is React**, built with Keycloakify and compiled into the Keycloak image
   as a login theme. The Authorization Code + PKCE redirect of §D2 is unchanged; only the
   page Keycloak renders changes.
3. **Phase 1 includes the data layer.** EF Core, the `ExternalSubject` columns, and
   first-login provisioning ship with the auth wiring rather than in a later phase, so the
   login flow terminates in a real local row.

Everything else in the 2026-08-24 design stands: two-realm separation as a structural
guarantee, BFF cookies rather than browser tokens, realm JSON in git, production seeding
from phase 1, no PHI in claims.

---

## 2. Realms, clients, and the structural guarantee

```
Keycloak
├── realm: anamnys-providers      clinicians, clinic admins, billing staff
│   └── roles: provider, clinic-admin, billing-staff
├── realm: anamnys-patients       patients only
│   └── roles: patient
└── realm: anamnys-owners         product owners and internal staff
    └── roles: owner, support, ops
```

| Client | Realm | Type | Flow |
|---|---|---|---|
| `anamnys-web` | providers | confidential (BFF) | Authorization Code + PKCE |
| `anamnys-patient-web` | patients | confidential (BFF) | Authorization Code + PKCE |
| `anamnys-admin-web` | owners | confidential (BFF) | Authorization Code + PKCE |
| `anamnys-api` | all three | bearer-only (audience) | — |

### The guarantee extends unchanged to the third realm

The 2026-08-24 argument was that a patient token cannot carry `provider` because it is
signed by the wrong key and fails validation before any role check executes. The owners
realm inherits exactly that property, and the API is wired so it is load-bearing:

**Every endpoint group names its accepted authentication schemes explicitly.** PHI groups
list only the providers and patients schemes. An owners-realm credential presented to a PHI
endpoint produces **401, not 403** — it is not an authenticated principal there at all,
because no registered scheme will accept its issuer. This is a missing scheme, not a failed
policy, and that distinction is the whole point.

### Scopes

The PHI vocabulary from §4 of the prior design is unchanged:

```
notes:read   notes:write   notes:sign
billing:read
patients:read
```

The owners realm gets a **disjoint** vocabulary. No owners-realm scope names a PHI resource:

```
tenancy:read   tenancy:write
billing:read   billing:write
plans:write
audit:read
ops:read
breakglass:request
```

`billing:read` appears in both vocabularies and means different things — a provider reads
their own billing codes, an owner reads platform revenue. They are different audiences on
different issuers hitting different endpoint groups, so the collision is nominal, not real.
It is called out here so nobody later "unifies" them.

---

## 3. BFF session mechanics

The prior design established cookies over browser tokens. Four SPAs on one origin make the
mechanics non-obvious, so they are fixed here.

### Cookie names, not cookie paths

Three cookie authentication schemes, distinguished by **name** at `Path=/`:

```
__Host-anamnys-provider
__Host-anamnys-patient
__Host-anamnys-owner
```

Path-scoping cookies to `/provider/`, `/patient/`, `/admin/` is the obvious idea and is
wrong: API calls go to `/api/*`, and a cookie scoped to `/provider` is not sent there. The
browser would authenticate the SPA shell and nothing else. Distinct names at root path
avoid this, and the `__Host-` prefix is free hardening — it forces `Secure`, forbids
`Domain`, and requires `Path=/`, which is what we want anyway.

All three are `HttpOnly`, `Secure`, `SameSite=Strict`.

### Three OIDC handlers

One per realm, each with its own callback path so the handlers cannot collide:

```
/signin-oidc-provider   → signs into __Host-anamnys-provider
/signin-oidc-patient    → signs into __Host-anamnys-patient
/signin-oidc-owner      → signs into __Host-anamnys-owner
```

Wired with `AddKeycloakOpenIdConnect` per the prior design §6. `RequireHttpsMetadata` is
true outside development, with an explicit `Authority`.

### Tokens live in Redis, not in the cookie

`SaveTokens = true` puts access and refresh tokens inside the cookie. Encrypted, but still
shipped to the browser on every request, and large enough to approach the 4KB cookie limit
once realm roles are in the token.

Instead: a server-side `ITicketStore` backed by the existing Redis resource, with
DataProtection keys also persisted to Redis
(`Microsoft.AspNetCore.DataProtection.StackExchangeRedis`). The cookie carries an opaque
session identifier and nothing else — which is what the prior design's §D2 diagram
actually depicts. Sessions then survive a server restart, and the design does not have to
be revisited when the server runs more than one replica.

### Refresh

In the cookie handler's `OnValidatePrincipal`: if the stored access token is expired or
near expiry, exchange the refresh token, update the ticket in place, and continue. If the
refresh fails, reject the principal and let the browser be challenged. The browser observes
nothing in either case.

### Bearer schemes still exist

`AddKeycloakJwtBearer` is registered per realm for the API split anticipated in the prior
design and for the React Native client of its §10.3. Nothing in the browser path uses it.

This retires the `?access_token=` SignalR weakness the prior design §D2 flagged: with
same-origin cookie auth, the WebSocket upgrade carries the session cookie and no token is
ever written into a query string or an access log.

---

## 4. The owners realm and PHI

### Standing access

Owners reach: tenancy, subscriptions and plans, billing and revenue, provider accounts and
profiles, usage counters, webhook events, audit logs, service health.

Owners do **not** reach: `Notes`, `NoteSections`, `Transcripts`, `ExtractedFacts`,
`PatientQuotes`, `SpeechMarkers`, `ClinicalDocuments`, `Sessions`, `TreatmentPlans`,
`RecordingConsents`, or `Patients` demographics. Not by role, not by policy — the endpoint
groups serving those resources do not accept the owners scheme.

This is the HIPAA minimum-necessary position (§164.502(b)) held structurally. An owner
account is not a workforce member with PHI access, so it does not carry the training,
sanction, and access-review obligations that a standing PHI grant would create.

### Break-glass

Support cannot be done with no path at all, so there is exactly one, and it is narrow,
time-boxed, two-person, and logged.

`BreakGlassGrants`:

| Column | Notes |
|---|---|
| `Id` | uuid pk |
| `StaffId` | who may use it |
| `ProviderId` | the tenant it is scoped to |
| `TicketRef` | required; the support ticket justifying it |
| `Reason` | required free text |
| `AuthorizedBy` | a *different* `StaffId`; enforced by check constraint |
| `AuthorizedAt` | |
| `ExpiresAt` | required; short by default |
| `RevokedAt` | nullable |

Rules:

1. A grant is exercisable **only** through an explicitly enumerated, read-only endpoint
   group at `/api/admin/break-glass/*`. Normal PHI endpoints remain unreachable to the
   owners scheme whether or not a grant exists. The grant widens a separate, deliberately
   small surface; it does not unlock the main one.
2. Every request through that group appends an `AccessLogs` row with `ActorType='staff'`,
   the `TicketRef`, and the `AuthorizedAt` of the grant. Those three columns already exist
   in the schema and are precisely this record.
3. `AuthorizedBy <> StaffId` is a database check constraint, so the two-person rule cannot
   be bypassed by an application bug.

**Phasing:** phase 1 ships the `Staff` and `BreakGlassGrants` tables, the grant lifecycle,
and the `AccessLogs` write path. The break-glass *endpoints* ship alongside the features
whose data they expose — there is no PHI in the system to reach in phase 1.

---

## 5. Schema amendments

The project is greenfield with no data (prior design §10.1), so these are edits to
`anamnys-db-script.sql`, not migrations. The script is the schema of record; leaving the
credential columns in place would materialise a `PasswordHash NOT NULL` column that the
design forbids and that the first EF model would have to work around.

### `Providers`

- **Drop** `PasswordHash`, `TwoFactorEnabled`, `TwoFactorSecretEncrypted`.
- **Add** `"ExternalSubject" uuid NOT NULL` with a unique index.

Non-nullable because a `Providers` row is *created by* a successful first login. There is
no window in which one exists without a Keycloak subject.

### `RecoveryCodes`

**Drop the table.** Keycloak owns recovery codes, as it owns passwords and TOTP secrets.

### `PatientAccounts`

- **Drop** `AuthMethod`, `EmailVerifiedAt`, `FailedAttempts`, `LockedUntil`. Keycloak owns
  the magic-link/OTP/password choice, email verification, and brute-force lockout.
- **Add** `"ExternalSubject" uuid` with a unique index, **nullable**.

Nullable, unlike the provider column, because a provider creates a patient record long
before that patient ever logs in — often before they have been invited at all. The column
is populated on the patient's first successful sign-in.

### `PatientAuthTokens`

**Drop the table.** Its `login` and `verify_email` purposes are Keycloak's. Its
`cancel_appointment` purpose is not authentication at all — it is an unauthenticated,
single-use deep link into a booking flow. If that feature ships it gets its own narrower
table, designed as a capability URL, rather than living in a table named for auth tokens.

### New: `Staff`

| Column | Notes |
|---|---|
| `Id` | uuid pk |
| `ExternalSubject` | uuid, not null, unique — owners-realm subject |
| `Email` | text, not null, unique |
| `Name` | text, not null |
| `Role` | text, check in (`owner`, `support`, `ops`) |
| `DisabledAt` | nullable |
| `CreatedAt` / `UpdatedAt` | |

Created by first-login provisioning against the owners realm, exactly as `Providers` is.

### New: `BreakGlassGrants`

Per §4 above, with `CONSTRAINT "BreakGlassGrants_TwoPerson_ck" CHECK ("AuthorizedBy" <> "StaffId")`.

### Deliberately unchanged

`ShareLinks.PasswordHash` stays. That is a password on a shared document link, not a user
credential, and Keycloak has no opinion about it.

`AccessLogs` stays exactly as written — its `ActorType` check already admits `'staff'`, and
its `TicketRef` / `AuthorizedAt` columns are the break-glass record. No amendment needed.

### Identity resolution

Unchanged in substance from the prior design §5: every provider-scoped query resolves
through `ExternalSubject`, never through a client-supplied id. The subject comes from the
authenticated session, which comes from the ticket store, which came from a token this
server obtained itself. There is no path by which a client supplies it.

---

## 6. The React login page

### Mechanism

Keycloakify compiles a React application into a Keycloak login theme. The browser still
redirects to Keycloak and still runs Authorization Code + PKCE; the page it lands on is
ours. This preserves every server-side mechanism the HIPAA story depends on — brute-force
lockout, required actions, TOTP and WebAuthn enrolment, password reset, email verification
— none of which exist if login is a form in the SPA posting to a direct grant.

The alternative considered and rejected was Resource Owner Password Credentials: the SPA
keeps its own login form and the BFF exchanges credentials for tokens. It is removed in
OAuth 2.1, disabled by default in Keycloak 26, and would require reimplementing password
reset, email verification, and step-up MFA in application code — the code this design
exists to avoid writing.

### Structure

A new workspace app, `apps/keycloak-theme`, consuming `@anamnys/shared` for `Button`,
`TextField`, and the i18n resources, so the existing visual design carries across rather
than being reinvented.

Pages overridden: `login`, `register`, `login-otp`, `login-reset-password`,
`login-update-password`, `login-verify-email`, `error`. Everything else falls through to
Keycloak's default theme.

**One theme, per-realm branding.** A single Keycloakify build; each realm JSON sets
`loginTheme: "anamnys"`, and the theme switches palette and logo on `kcContext.realm.name`.
Three separate themes would triplicate the component code for a difference that is a CSS
variable.

### Consequences for the provider SPA

- `apps/provider/src/routes/_auth/login.tsx` and `_auth/register.tsx` are deleted; those
  flows now live in the theme. `_auth.tsx`'s layout is the reference the theme's layout is
  ported from.
- `packages/shared/src/lib/store/authStore.ts` loses `login`, `register`,
  `completeTwoFactorLogin`, `cancelTwoFactor`, and `pendingTwoFactor`. Two-factor is a step
  in Keycloak's flow now, not a state in the client.
- It keeps `user`, `isLoading`, `error`, `loadUser`, and `logout`, and gains a `login()`
  that is a full-page navigation to `/auth/provider/login` — not a fetch. The response is a
  302 to Keycloak, which XHR cannot follow usefully.
- `packages/shared/src/api/auth.ts` reduces to `me()` and `logout()`.

### Known risk

Keycloakify 11.16.0 declares no `peerDependencies` and is developed against Vite 5,
React 18, and TypeScript 4.9. This repository is on Vite 8, React 19, and TypeScript 6.0.3
(the last pinned deliberately — see `CLAUDE.md`). Nothing will *block* installation; the
question is whether Keycloakify's Vite plugin works against Vite 8's plugin API.

**This is the first task in the implementation plan, run as a spike before anything depends
on it.** If it fails, `apps/keycloak-theme` pins its own Vite and TypeScript in its
`package.json`. npm workspaces permits this, and the theme builds independently of the
three SPAs, so a divergent build toolchain there costs nothing elsewhere.

---

## 7. AppHost and seeding

### Resource graph

```
postgres (container, WithDataVolume)
├── keycloakdb
└── anamnysdb
keycloak   ← WithDockerfile("./keycloak"), fixed port 8080, Postgres-backed, OTLP
server     ← references anamnysdb, keycloak, cache
web · provider · patient · admin
```

`admin` is a new Vite app at `apps/admin`, `base: '/admin/'`, published via
`server.PublishWithContainerFiles(admin, "wwwroot/admin")` — the same pattern the three
existing SPAs already use, for the same same-origin reason.

Keycloak keeps the fixed port 8080 from the prior design §6: a shifting port invalidates
cookies and redirect URIs across AppHost restarts.

### Verified package versions

Checked against nuget.org on 2026-09-05, not from memory:

| Package | Version |
|---|---|
| `Aspire.Hosting.Keycloak` | 13.5.3-preview.1.26425.3 |
| `Aspire.Keycloak.Authentication` | 13.5.3-preview.1.26425.3 |
| `Aspire.Hosting.PostgreSQL` | 13.5.3 |
| `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL` | 13.5.3 |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.3 |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.11 |
| `Microsoft.AspNetCore.Authentication.OpenIdConnect` | 10.0.11 |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.11 |
| `Microsoft.AspNetCore.DataProtection.StackExchangeRedis` | 10.0.11 |
| `CommunityToolkit.Aspire.Hosting.Keycloak.Extensions` | 13.5.1-beta.736 |

**On the beta.** `Aspire.Hosting.Keycloak` 13.5.3 was inspected directly: its public surface
is `AddKeycloak`, `WithRealmImport`, `WithDataVolume`, `WithArgs`, `WithContainerFiles`, and
the standard container extensions. There is **no** database method. `WithPostgres` and
`WithPostgresData` exist only in the CommunityToolkit package, which does ship a `net10.0`
target. If the beta proves unacceptable, the fallback is setting `KC_DB`, `KC_DB_URL`,
`KC_DB_USERNAME`, and `KC_DB_PASSWORD` directly through `WithEnvironment` — the toolkit
extension is a convenience over exactly those variables.

### Seeding

`anamnys-aspire/keycloak/Dockerfile`:

```dockerfile
FROM quay.io/keycloak/keycloak:26.6
COPY ./realms/*.json /opt/keycloak/data/import/
COPY ./theme/keycloak-theme-anamnys.jar /opt/keycloak/providers/
```

wired with `.WithDockerfile("./keycloak")`, which preserves `AddKeycloak`'s startup
arguments.

The theme is shipped as Keycloakify's built jar into `/opt/keycloak/providers/`, not as an
unpacked directory under `/opt/keycloak/themes/`. The jar is what `npm run build-keycloak-theme`
in `apps/keycloak-theme` emits, and Keycloak's provider loading picks it up without a
`--spi-theme` argument that would fight `AddKeycloak`'s defaults.

**Build ordering.** `keycloak/theme/*.jar` is a build output, so the Docker build depends on
the theme build having run. Two consequences: the jar is gitignored, and the AppHost must
not be the only thing that knows how to produce it — `apps/keycloak-theme` builds via the
root `npm run build` workspace script like every other app, and the Dockerfile fails loudly
if the jar is absent rather than silently producing a Keycloak with the stock login page.

`WithRealmImport` is **not** used. The prior design §7 establishes why: it is
development-only, silently dropped by `aspire publish`, and its failure mode is a green
dashboard with every login 404ing. The custom image ships from phase 1 so development and
production seed by the identical mechanism.

Three realm JSON files live in `keycloak/realms/` and are reviewed as source. The MFA
requirement and password policy are therefore visible in a diff, which is itself a
defensible HIPAA control.

**No client secrets in committed realm JSON.** Secrets are `${ANAMNYS_PROVIDER_CLIENT_SECRET}`-style
env placeholders fed by Aspire parameters. Admin-UI realm exports are noisy and may embed
secrets — curate before committing.

**Gotcha to document in `CLAUDE.md`:** realm import runs only when the realm does not
already exist. With `WithDataVolume()`, edits to realm JSON do not apply until the volume is
deleted.

---

## 8. HIPAA control mapping

Extends the prior design §8 rather than replacing it.

| Control | Requirement | Mechanism |
|---|---|---|
| Access control | §164.312(a)(1) | Three realms, three issuers; endpoint groups name accepted schemes, so a wrong-realm credential yields 401 before any policy runs; query-level provider scoping through `ExternalSubject` |
| Minimum necessary | §164.502(b) | Owners realm has no standing PHI access and a disjoint scope vocabulary |
| Person/entity authentication | §164.312(d) | MFA required in providers and owners realms (TOTP/WebAuthn), available in patients; enrolment is a Keycloak required action, reachable because login stays a redirect |
| Audit controls | §164.312(b) | Keycloak event logging persisted and exported (default retention is short and **must** be configured); every break-glass read appends `AccessLogs` |
| Emergency access | §164.312(a)(2)(ii) | Time-boxed, two-person, ticket-referenced break-glass grants over an enumerated read-only endpoint group |
| Transmission security | §164.312(e) | HTTPS throughout; `RequireHttpsMetadata = true` outside development with explicit `Authority` |

**No PHI in token claims.** Subject, roles, and scopes only. Tokens reach logs and browser
history.

---

## 9. Testing

The tests that matter are the ones proving the negative:

1. An owners-realm session presented to a provider PHI endpoint returns **401** — asserting
   the status code specifically, because a 403 would mean the scheme accepted the issuer and
   only a policy stopped it. The distinction is the design.
2. Likewise patient session against a provider endpoint, and provider session against
   `/api/admin/*`.
3. Cross-provider denial: provider A cannot read provider B's rows, with the provider id
   resolved from the session and never from the request. Warranted independently of realm
   separation, per the prior design §5.
4. First-login provisioning is idempotent — a second login does not create a second row.
5. A `BreakGlassGrants` insert with `AuthorizedBy = StaffId` is rejected by the database.
6. Refresh: an expired access token in the ticket store is transparently renewed and the
   request succeeds; a failed refresh challenges rather than 500s.

xUnit with FluentAssertions, per `CLAUDE.md`.

---

## 10. Phase 1 definition of done

- A provider signs in through the React Keycloakify page.
- The BFF holds access and refresh tokens in the Redis ticket store; the browser holds only
  `__Host-anamnys-provider`.
- A `Providers` row is provisioned on first login with `ExternalSubject` populated.
- `/api/auth/me` returns that row.
- Logout clears the session and performs RP-initiated logout at Keycloak.
- Test 1 from §9 passes: an owners cookie gets 401, not 403, from a provider endpoint.
- Realm JSON, the Dockerfile, and the theme build all ship; `aspire publish` produces a
  Keycloak image with the realms and theme baked in.

## 11. Out of scope

Unchanged from the prior design §9 — third-party API access, SSO federation with clinic
IdPs, credential migration — plus:

- The `apps/admin` SPA is scaffolded and authenticates, but its features are not built here.
- Break-glass endpoints, per §4.
- The React Native client of the prior design §10.3.
