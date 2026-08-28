# Authentication & Authorization Design

**Date:** 2026-08-24
**Status:** Approved (design); implementation not started
**Scope:** Identity, authentication, and authorization for the Anamnys Aspire application

---

## 1. Context

Anamnys is a clinical note drafting system handling PHI for mental health and
physical therapy practices. The architecture's founding decision is that **PHI
never leaves our infrastructure** — Whisper and LLaMA run in-process specifically
to avoid Business Associate Agreements with third-party AI vendors.

Requirements driving this design:

| Requirement | Answer |
|---|---|
| Who authenticates | **Clinicians/staff and patients, both from day one** |
| Deployment | **Self-hosted, own infrastructure** |
| API shape | **One API now, designed to split later** |
| Frontends | Multiple (web app, marketing, mobile later) |
| Jurisdictions | US (HIPAA), Canada, Brazil (LGPD), Europe (GDPR) |

### Why not a hosted IdP

Clerk was evaluated. Its HIPAA BAA is **Enterprise-tier only** (custom, annual
billing) per clerk.com/pricing. Beyond cost, two structural problems:

1. **Patients authenticating means the IdP holds PHI.** The association between a
   named person and a mental-health practice is PHI on its own — no clinical note
   required. A hosted IdP would reintroduce exactly the third-party PHI dependency
   the Whisper/LLaMA decision exists to avoid.
2. **Self-hosted deployment means no cloud BAA umbrella.** There is no Azure or AWS
   BAA covering a managed IdP in this topology.

Keycloak is self-hosted, requires no BAA, has a first-party Aspire hosting
integration, and solves EU/Brazil data residency by deployment location rather
than vendor contract.

---

## 2. Decisions

### D1 — Two realms, not one

```
Keycloak
├── realm: anamnys-providers      clinicians, clinic admins, billing staff
│   └── roles: provider, clinic-admin, billing-staff
└── realm: anamnys-patients       patients only
    └── roles: patient
```

Separate realms mean **separate user stores, separate token signing keys, and
separate issuers**. A patient token cannot carry `provider`: it is signed by the
wrong key and fails validation before any role check executes. This is a
structural guarantee rather than a policy check, which is a materially stronger
argument for HIPAA §164.312(a)(1) access control.

It also permits per-population password policy, MFA requirements, session
lifetimes, and branding.

**Accepted cost:** a clinician who is also a patient needs two accounts. For a
mental-health product this separation is arguably correct regardless.

### D2 — Backend-for-Frontend (BFF) with cookies, not tokens in the browser

The SPA is served from the .NET server's `wwwroot` (via the stable
`PublishWithContainerFiles`), so SPA and API are **same-origin** in production.

```
Browser ──HttpOnly, Secure, SameSite=Strict cookie──▶ .NET server (BFF)
                                                          │ holds access + refresh tokens
                                                          ▼
                                                     Keycloak / APIs
```

- Tokens never enter JavaScript. An XSS bug cannot exfiltrate them.
- No CORS, no silent-refresh iframes, no `localStorage`.
- Refresh happens server-side; the browser only ever holds an opaque session cookie.

**This also removes an existing weakness.** The current system passes JWTs to
SignalR as `?access_token=…` because WebSocket upgrades cannot send headers.
Same-origin cookie auth eliminates that, and with it access tokens being written
into server access logs — a real PHI-adjacent exposure today.

---

## 3. Client model

| Client | Realm | Type | Flow |
|---|---|---|---|
| `anamnys-web` | providers | confidential (BFF) | Authorization Code + PKCE |
| `anamnys-patient-web` | patients | confidential (BFF) | Authorization Code + PKCE |
| `anamnys-api` | both | bearer-only (audience) | — |

The BFF is a confidential client because the .NET server holds the secret. No
public browser client, no implicit flow.

## 4. Scopes and audiences

One audience today (`anamnys-api`), with a scope vocabulary that survives
decomposition:

```
notes:read   notes:write   notes:sign
billing:read
patients:read
```

Splitting out a billing service later adds an audience. It does not re-model
scopes or require re-issuing tokens.

Roles map to ASP.NET Core policies; scopes gate individual endpoints.

## 5. Identity mapping

**Greenfield — there is no existing data or accounts.** There is no migration and
no legacy `sub` = `Provider.Id` contract to unwind. The mapping is designed
correctly from the first commit.

Design:
- `Provider.ExternalSubject` — Keycloak user UUID, unique index, non-nullable
- Equivalent on the patient entity
- First-login provisioning creates the local row on first successful sign-in
- Every provider-scoped query resolves through `ExternalSubject`, never through a
  client-supplied id

**The application never stores credentials.** Keycloak owns passwords, hashing,
reset flows, and lockout. No password column, no bcrypt, no reset tokens in the
application database.

Provider-scoping remains the system's primary access control, so it still warrants
dedicated tests proving cross-provider access is impossible — but as
build-it-right work, not risky migration work.

## 6. AppHost wiring

```csharp
var keycloakDb = postgres.AddDatabase("keycloakdb");

var keycloak = builder.AddKeycloak("keycloak", 8080)   // stable port: cookies outlive the AppHost
    .WithPostgres(keycloakDb)                          // CommunityToolkit.Aspire.Hosting.Keycloak.Extensions
    .WithDataVolume()
    .WithOtlpExporter();                               // auth flows visible in the dashboard

var server = builder.AddProject<...>("server")
    .WithReference(keycloak)
    .WaitFor(keycloak);
```

API side uses `Aspire.Keycloak.Authentication`:
- `AddKeycloakJwtBearer(serviceName: "keycloak", realm: …)` — API token validation
- `AddKeycloakOpenIdConnect(…)` — the BFF's login leg

## 7. Realm seeding

`WithRealmImport` is **development-only** — the docs state it uses development-time
container file injection and is not supported under `aspire publish`/`deploy`.

**The failure mode is silent.** Keycloak starts, its `/health/ready` check passes
(it reports Keycloak readiness, not realm existence), the dashboard shows green —
and every login 404s while the API cannot fetch JWKS, so all requests 401.

**Therefore the production seeding mechanism ships from phase 1**, so dev and
production seed identically:

```dockerfile
FROM quay.io/keycloak/keycloak:26.6
COPY ./realms/*.json /opt/keycloak/data/import/
```

wired with `WithDockerfile("./keycloak")`, which preserves `AddKeycloak`'s
defaults including startup arguments.

Realm JSON is committed to git and treated as reviewed source. The MFA
requirement and password policy then become visible in a diff — itself a
defensible HIPAA control.

**Hard rule:** no client secrets in committed realm JSON. Use env-var
placeholders fed by Aspire parameters. Admin-UI realm exports are noisy and may
embed secrets — curate before committing.

**Known gotcha:** realm import runs only when the realm does not already exist.
With `WithDataVolume()`, edits to the JSON will not apply until the volume is
deleted. Document this for the team.

## 8. HIPAA control mapping

| Control | Requirement | Mechanism |
|---|---|---|
| Access control | §164.312(a)(1) | Realm separation + role/scope policies + query-level provider scoping |
| Person/entity authentication | §164.312(d) | MFA required for providers realm (TOTP/WebAuthn); available for patients |
| Audit controls | §164.312(b) | Keycloak event logging persisted and exported — **default retention is short and must be configured** |
| Transmission security | §164.312(e) | HTTPS throughout; `RequireHttpsMetadata = true` in production with explicit Authority |

**No PHI in token claims.** `sub`, roles, and scopes only — no names, no
diagnoses. Tokens reach logs and browser history.

## 9. Out of scope

- Third-party/external API access (client credentials, per-tenant clients, rate limiting)
- SSO federation with clinic identity providers
- Credential migration — **not applicable**; greenfield, no existing accounts (see §10)

## 10. Open questions

1. ~~**Existing users.**~~ **Resolved 2026-08-25:** the project is starting from the
   ground up with no accounts and no data. No credential migration, no Keycloak
   custom hash provider, no `ExternalSubject` backfill.
2. **Marketing site.** Does it need auth at all, or does it just link to the app?
3. **Mobile client.** A React Native client is part of the product intent. If it
   ships, it needs its own OIDC client and cannot use the BFF cookie pattern — it
   would use Authorization Code + PKCE with tokens in secure device storage.

## 11. Risks

| Risk | Severity | Mitigation |
|---|---|---|
| Provider scoping implemented incorrectly | Medium | Greenfield, so no migration risk; dedicated tests proving cross-provider denial before any PHI is loaded |
| Aspire Keycloak hosting integration is Preview (`13.5.2-preview.1`) | Low | Affects dev-time orchestration only; Keycloak itself is mature. No non-preview alternative exists |
| Realm config drift between environments | Medium | Realm JSON in git; identical seeding mechanism dev and prod |
| Keycloak operational burden (upgrades, HA, backups, key rotation) | Medium | Accepted deliberately; auth is materially easier to self-host than the AI layer already is |

## 12. Phasing

1. Keycloak resource + providers realm + custom seeding image + BFF login/logout — providers only
2. `ExternalSubject` on the domain entities + first-login provisioning + policy-based
   authorization on endpoints
3. Patients realm + patient-facing routes
4. MFA enforcement + audit event export + production hardening
