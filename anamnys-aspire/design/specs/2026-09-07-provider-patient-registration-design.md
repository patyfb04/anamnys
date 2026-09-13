# Provider & Patient Self-Registration Design

**Date:** 2026-09-07
**Status:** Proposed
**Scope:** Public self-registration ("Comecar") for the `anamnys-providers` and
`anamnys-patients` realms, delivered as two phases. Extends
`2026-08-24-authentication-design.md` and `2026-09-05-keycloak-implementation-design.md`
rather than replacing them.

---

## 1. Context

`apps/web`'s marketing CTAs ("Comecar" / "Get Started") currently link to
`/provider/register`, a path with no route on either side — a pre-existing dead link.
Separately, `apps/web`'s "Entrar" link has the same problem pointing at
`/provider/login`; that fix is tracked separately and is **not** part of this spec.

This design wires "Comecar" to real self-registration through Keycloak, for the two
realms where self-registration is consistent with the approved provisioning model
(`FirstLoginProvisioner`, see `08-session-lifecycle-in-code.md`):

- **Provider** — already `create-on-first-login`. Self-registration requires no change
  to provisioning logic, only a Keycloak-side registration form and a way to reach it.
- **Patient** — currently **bind-only, never create** (a `PatientAccount` row must
  already exist, created by a provider invite). Supporting self-registration means
  adding a third branch to `FirstLoginProvisioner.ResolvePatientAsync`: create a new row
  when no bound or unclaimed row matches, alongside the existing invite-bind path, which
  stays exactly as it is today.

**Explicitly out of scope:**
- **Administrador (owners realm).** Per the approved design, owners-realm accounts are
  internal staff, role-gated, never publicly self-served. No registration path is added
  for this realm; `registrationAllowed` stays `false`.
- **Fixing "provider invites a patient."** `apps/provider/src/routes/_app/patients/new.tsx`
  today posts to a `/patients` endpoint that does not exist and doesn't collect an email,
  so it can never actually produce a bindable `PatientAccount` row. This is a real,
  separate gap and gets its own spec later — this design does not touch it.
- **"Entrar" / login picker.** Parked in an earlier discussion; not part of this change.

## 2. Phasing

**Phase 1 (this implementation pass): Provider only.**
**Phase 2 (follow-up): Patient.**

The UI ships the full two-persona picker now (so the "Comecar" flow doesn't need
reshaping twice), but the Paciente option is visibly disabled ("Em breve") until Phase 2
lands — its realm keeps `registrationAllowed: false`, so nothing in Phase 1 can produce a
half-working patient signup.

## 3. End-to-end flow (Phase 1, Provider)

```
apps/web "Comecar" (Link, same-app nav)
  └─▶ /get-started  — new route, persona picker: Profissional (live) / Paciente (disabled)
        └─▶ Profissional clicked → <a href="/auth/provider/register"> (full-page nav)
              └─▶ GET /auth/provider/register (new server endpoint)
                    └─▶ Results.Challenge(provider-oidc), redirect rewritten to
                        Keycloak's .../protocol/openid-connect/registrations
                          └─▶ Keycloak renders our themed Register.tsx (register.ftl)
                                └─▶ user submits — Keycloak creates the credential;
                                    no data crosses our server at this step
                                      └─▶ redirect back through /signin-oidc-provider
                                            └─▶ OnTokenValidated → FirstLoginProvisioner
                                                .ProvisionAsync (Providers branch,
                                                unchanged) creates the Provider row from
                                                the token's email/name claims
                                                  └─▶ cookie issued → SafeLocalRedirect
                                                      default appPath "/provider/" →
                                                      index.tsx redirects to /dashboard
                                                      (new placeholder route)
```

Nothing here bypasses the BFF/no-tokens-in-browser model: the registration form posts
natively to Keycloak (`url.registrationAction`, the same pattern `Login.tsx` already uses
for `url.loginAction`), never to our API.

## 4. Components touched (Phase 1)

- **`keycloak/realms/anamnys-providers.json`** — `registrationAllowed: true`;
  `registrationEmailAsUsername: true` (this app has no separate username concept; login
  already treats email as the identifier via `loginWithEmailAllowed: true`).
- **`anamnys-aspire.Server/Auth/AuthEndpoints.cs`** — `MapRealm` gains a
  `GET /auth/{segment}/register` route, mounted for all three realms (mechanically
  symmetric with `/login`), guarded by `AllowAnonymous` and the same `SafeLocalRedirect`.
  It only actually succeeds where the realm's `registrationAllowed` is `true` — Keycloak
  itself is the gate, not realm-conditional C#.
- **`anamnys-aspire.Server/Auth/AuthenticationSetup.cs`** — a new
  `OnRedirectToIdentityProvider` handler per realm wiring: if the challenge's
  `AuthenticationProperties.Items` carries a `"register"` marker (set by the new
  endpoint), rewrite `context.ProtocolMessage.IssuerAddress` from
  `.../protocol/openid-connect/auth` to `.../protocol/openid-connect/registrations`
  before the redirect is issued. Inert for every request that doesn't set the marker.
- **`apps/keycloak-theme/src/login/pages/Register.tsx`** (new) — built on Keycloakify's
  `UserProfileFormFields` (the declarative-user-profile approach Keycloak 26 expects for
  `register.ftl`, already imported in `KcPage.tsx` for the default-theme fallback),
  wrapped in the existing `Template` shell like `Login.tsx`. Fields: email, first name,
  last name, password, password confirmation — matching exactly what `Provider` has
  columns for (`Email`, `Name`); no extra attributes collected since `Specialty` and
  `PreferredNoteFormat` already default sensibly and are editable later in settings.
  Reuses the existing `sign-up.jpg` asset already sitting in `Template.tsx`'s hero.
- **`apps/keycloak-theme/src/login/KcPage.tsx`** — add
  `case 'register.ftl': return <Register .../>` and its lazy import.
  **`apps/keycloak-theme/src/login/i18n.ts`** — add the handful of custom strings
  `Register.tsx` needs beyond Keycloak's built-in message bundle (English only, matching
  this theme's current state — the realm has no locale config today, a pre-existing gap
  this spec doesn't fix).
- **`apps/web/src/routes/_marketing/get-started.tsx`** (new route, `/get-started`) —
  persona picker. Profissional links to `/auth/provider/register`; Paciente renders
  disabled with an "Em breve" label. File/route naming stays English per this repo's
  convention even though the displayed labels are Portuguese.
- **`packages/shared/src/lib/appUrls.ts`** — `providerRegister` becomes
  `/auth/provider/register` (used by the picker's Profissional button); a new
  `getStarted: "/get-started"` constant (matching the existing `welcome.getStarted` i18n
  key). `MarketingHeader.tsx`, `index.tsx` (×2), `product.tsx` (×2) switch their "Comecar"
  CTA from `<a href={appUrls.providerRegister}>` to `<Link to={appUrls.getStarted}>` —
  same-app nav now that there's an intermediate page, same reasoning `MarketingHeader`'s
  existing comment already gives for cross-app links needing `<a>` and same-app ones
  needing `<Link>`.

No change to `FirstLoginProvisioner` in Phase 1 — the Providers branch already does
exactly what's needed.

**Provider dashboard placeholder.** Landing a freshly-registered provider on `/patients`
(today's behavior — `index.tsx` redirects there because it's simply the first item in
`Sidenav`'s nav list, not because it's meant to represent a dashboard) doesn't read as a
real post-signup landing. `specs/UI/Dashboard/screen.png` shows the actual intended
dashboard — greeting, stats tiles, quick actions, today's schedule, recent activity — but
every one of those depends on features that don't exist server-side yet (transcription,
notes, scheduling). Building that is its own design pass. For this phase:

- **`apps/provider/src/routes/_app/dashboard.tsx`** (new) — minimal placeholder inside the
  existing `_app` layout (so it gets `Sidenav`/`TopBar`/`AppTabBar` chrome for free): a
  greeting using `user.name` and a "coming soon" note. No stats, no schedule, no mock data.
- **`apps/provider/src/routes/index.tsx`** — redirect target changes from `/patients` to
  `/dashboard`.
- **`packages/shared/src/ui/Sidenav.tsx`** — new unconditional `NAV_ITEMS` entry,
  `{ href: "/dashboard", Icon: LayoutDashboard, labelKey: "nav.dashboard", matchPrefix:
  "/dashboard" }`, first in the list (matches the mockup's nav order) — `Patients` stays
  where it is otherwise, no reordering of the rest.
- **`packages/shared/src/lib/i18n/locales/pt.json`** — new `nav.dashboard: "Painel"` key.

## 5. Phase 2 sketch (Patient — not implemented this pass)

- `anamnys-patients.json`: `registrationAllowed: true`.
- `FirstLoginProvisioner.ResolvePatientAsync`: add a third branch — no bound row, no
  unclaimed row matching email → create a new `PatientAccount` (mirroring the Providers
  branch's create-and-handle-the-unique-index-race shape), still requiring
  `email_verified: true` off the token, consistent with the existing bind path's
  reasoning.
- A patient-flavored `Register.tsx` variant (or the same component, since
  `PatientAccount` needs nothing beyond email/name/password today — `Phone` and
  `TermsAcceptedAt` stay unset at registration, filled in later where relevant).
- `/get-started`'s Paciente button goes live, linking to `/auth/patient/register`.

## 6. Testing

No test infrastructure exists yet in `apps/web`, `apps/keycloak-theme`, or for the auth
endpoints in `anamnys-aspire.Server` (confirmed by search — zero `*.test.*` files in the
first two; no xUnit test class covering `AuthEndpoints`/`AuthenticationSetup` in the
third). Verification for this pass is `npm run build` + `npm run lint` in the affected
workspaces, plus a manual signup click-through against a local `aspire start` — following
the existing bar for this part of the codebase rather than introducing a new one
unilaterally.

## 7. Risks

- **Keycloakify + Vite 8/React 19/TypeScript 6.0.3 compatibility** was flagged as a spike
  in the prior design and is already resolved in this repo (`Login.tsx`, `LoginOtp.tsx`,
  `LoginResetPassword.tsx` all build today) — low incremental risk adding one more page in
  the same pattern.
- **`registrationEmailAsUsername: true` is a realm-config change**, visible in the realm
  JSON diff per the existing "config changes are code review, not a runtime toggle"
  principle — flagged here explicitly rather than folded in silently.
- **Realm import only runs when the realm doesn't already exist** (Chapter 7 gotcha) — this
  change requires removing the Keycloak/Postgres data volumes locally before it takes
  effect, same as any other realm JSON edit.
