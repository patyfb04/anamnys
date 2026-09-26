# Account Self-Service Design (Dados pessoais / Acesso e segurança)

**Date:** 2026-09-26
**Status:** Proposed
**Scope:** Lets a signed-in provider or patient edit their own profile data and manage
their password and two-factor authentication, from an account menu shared by
`apps/provider` and `apps/patient`. Owners (`apps/admin`) are out of scope.

---

## 1. Context

The shared `AccountMenu` (avatar in the top bar) currently offers "Acesso e segurança",
which goes through `GET /auth/{realm}/account` to Keycloak's account console. That
console fails in the browser with a generic "Algo deu errado" and is not themed. There
is no way for a user to change their own name, CRP or phone at all.

Two facts about the current system shape this design:

- **Email is the Keycloak login.** Every realm has `registrationEmailAsUsername: true`
  and `duplicateEmailsAllowed: false`. Changing an email only in the application
  database would leave Keycloak authenticating the old address.
- **The application database owns everything else after the first login.**
  `FirstLoginProvisioner` reads `name`, `crpNumber`, `crpRegion`, `given_name` and
  `family_name` from the token exactly once, when it creates the local row. Nothing reads
  those claims again. Editing them in the database is therefore safe; Keycloak's copies
  simply go stale (they only appear in emails Keycloak itself sends).

## 2. Decisions

| Question | Decision |
|---|---|
| How does email change? | Through Keycloak's `UPDATE_EMAIL` required action. Keycloak rejects an address already used in the realm and sends a confirmation link to the **new** address; the change applies only after that link is clicked. |
| How do password and 2FA change? | Through Keycloak's `UPDATE_PASSWORD` and `CONFIGURE_TOTP` required actions, rendered by the Anamnys login theme. The account console is no longer linked. |
| Which fields are editable in the app? | Provider: name, CRP number, CRP region. Patient: first name, last name, phone, date of birth. |
| Where do those edits live? | The application database only. |

## 3. Account menu

`packages/shared/src/ui/AccountMenu.tsx`, both apps, in this order:

1. Name and email (display only)
2. **Dados pessoais** → `/account/profile` (in-app route)
3. **Acesso e segurança** → `/account/security` (in-app route)
4. **Sair**

`AccountMenu` uses TanStack `<Link>` for items 2 and 3, the same way `Sidenav` links with
string hrefs. Each app mounts both routes under its own authenticated `_app` layout.

## 4. Server: application-initiated actions

New route in `AuthEndpoints.MapRealm`, mounted for every realm like `/login`:

```
GET /auth/{segment}/action/{action}?returnUrl=/provider/account/security
```

- `action` must be one of `UPDATE_PASSWORD`, `CONFIGURE_TOTP`, `UPDATE_EMAIL`. Anything
  else returns `400`. The allowlist exists because the value is forwarded verbatim to
  Keycloak as `kc_action`.
- Requires an authenticated session for that realm's cookie scheme. Without one it returns
  `401`, like every other cookie-protected route (`OnRedirectToLogin` is overridden).
- `returnUrl` goes through the existing `SafeLocalRedirect`, falling back to the realm's
  app path.
- Issues `Results.Challenge` on the realm's OIDC scheme with
  `Items["kc_action"] = action`. `AuthenticationSetup`'s `OnRedirectToIdentityProvider`,
  which already reads `kc_action` for `register`, is extended: for any other value it
  adds `kc_action=<value>` to the authorization request parameters.

Keycloak runs the action against the live SSO session, then completes an ordinary
authorization-code response to the BFF callback. The BFF signs the cookie in again with
fresh tokens, which is what lets the email sync in section 6 run. A cancelled action
returns the same way (Keycloak appends `kc_action_status=cancelled`); no special
handling is needed because the session is unchanged.

`GET /auth/{segment}/account` and its test (`AccountEndpointTests`) are removed, along
with the `.http` entries for it.

**Realm configuration:** the `UPDATE_EMAIL` required action is currently disabled in
every realm. The providers and patients realm JSON gain a `requiredActions` entry
enabling it. The running dev Keycloak is updated with `kcadm` (realm import does not
re-run on an existing realm); a fresh volume gets it from the JSON.

## 5. Server: profile API

In the existing `/api/phi` group (provider and patient cookie schemes):

```
GET /api/phi/me/profile
PUT /api/phi/me/profile
```

The realm is resolved the same way `/api/auth/me` does it: by which cookie scheme
authenticated the request. The local row is found through `principal.LocalIdOrNull()`,
never through a client-supplied id, preserving provider scoping.

**Provider** — `GET` returns `{ email, name, crpNumber, crpRegion }`. `PUT` accepts
`{ name, crpNumber, crpRegion }`.

**Patient** — `GET` returns `{ email, firstName, lastName, phone, dateOfBirth }`. `PUT`
accepts `{ firstName, lastName, phone, dateOfBirth }`.

Email is never accepted by `PUT`; it changes only through section 4.

**Validation** (manual, no FluentValidation yet), mirroring the Keycloak user profile
config where a rule exists there:

- `name`, `firstName`, `lastName`: required after trimming, at most 255 characters.
- `crpNumber`: 1–20 characters; `crpRegion`: 1–10 characters. Both set or both empty,
  matching the `Providers_Crp_ck` check constraint.
- `phone`: optional, at most 30 characters, digits and `+ ( ) - space` only.
- `dateOfBirth`: optional, not in the future.

A failure returns `Results.ValidationProblem` (`400`) with per-field messages. Success
returns `204` and updates `UpdatedAt`.

## 6. Email sync on login

`FirstLoginProvisioner` currently looks a returning user up by `ExternalSubject` and
returns. For providers and patients it will also compare the token's `email` with the
stored one:

- If they differ **and** `email_verified` is `true`, update the row's `Email` and
  `UpdatedAt`.
- If saving fails because another row already has that email (the unique index on
  `Email`), which can only happen with an unclaimed patient invite row, keep the old
  value and log a warning with the row id only, no email address (no PHI in logs).

The login itself is never blocked by this sync.

## 7. Frontend

**Shared** (`packages/shared`):
- `ui/AccountMenu.tsx` gets the menu from section 3.
- `components/SecurityPage.tsx` shows two cards, "Alterar senha" and "Configurar
  autenticação em dois fatores". Each is a full-page `<a href>` to
  `/auth/{realm}/action/{UPDATE_PASSWORD|CONFIGURE_TOTP}?returnUrl=<current path>`.
- `api/profile.ts` has `get` / `update` against `/api/phi/me/profile`, typed per realm.

**Per app** (`apps/provider`, `apps/patient`):
- `routes/_app/account/profile.tsx` renders the realm's form with TanStack Query
  (`useQuery` + `useMutation`). The email is read-only, next to an "Alterar email"
  link to `/auth/{realm}/action/UPDATE_EMAIL`. Field errors from a `400` show under
  their inputs.
- `routes/_app/account/security.tsx` renders the shared `SecurityPage`.

The provider's desktop and mobile `TopBar` rows keep passing `realm="provider"`; the
patient's `PatientTopBar` passes `realm="patient"`. User-facing text goes into
`pt.json`.

## 8. Testing

Integration tests (`anamnys-aspire.Tests`, shared AppHost fixture):

- `AuthActionEndpointTests`: an allowed action redirects to Keycloak's authorization
  endpoint with `kc_action=<action>`; an unknown action returns `400`; no session
  returns `401`.
- `ProfileEndpointTests`: no session returns `401`; an invalid `PUT` returns `400` with
  the offending field; a valid provider `PUT` persists and a following `GET` returns it.
- Email sync: a returning provider whose verified token email differs gets the new
  email; a conflict with an unclaimed patient row leaves the old email and does not
  fail the login.

Frontend: build and lint for all workspaces, then a manual pass in the browser for both
apps (menu, profile save, each Keycloak action and the return to the app).

## 9. Out of scope

- Showing whether 2FA is currently enabled (needs the Keycloak Admin API).
- The owners realm / `apps/admin`.
- Pushing profile edits back to Keycloak's user attributes.
- Theming Keycloak's account console (no longer linked).
