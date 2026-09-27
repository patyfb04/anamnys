# Admin (Owners Realm) Registration and Login Design

**Date:** 2026-09-27
**Status:** Proposed
**Scope:** Self-registration and login for the internal admin app (`apps/admin`, realm
`anamnys-owners`). New accounts start **pending** and gain access only after a staff role
is assigned manually in the Keycloak admin console. An in-app approval screen is out of
scope (a later feature).

---

## 1. Context

- The admin app already logs in: `apps/admin/src/routes/index.tsx` redirects every
  anonymous visitor straight to `/auth/owner/login`.
- Registration does not exist: `keycloak/realms/anamnys-owners.json` has
  `registrationAllowed: false`. The BFF already mounts `/auth/owner/register`
  (`AuthEndpoints.MapRealm`), and Keycloak refuses to render the form while the realm
  disallows it.
- `FirstLoginProvisioner.ProvisionStaffAsync` refuses any owners-realm login whose token
  carries none of `owner`, `support`, `ops`. A self-registered account would therefore
  land on `AuthErrorNotice` today.
- The marketing site's `/sign-in` and `/get-started` pickers deliberately do not offer
  the owners realm, and the Keycloak theme's `Template.tsx` hides the site navigation
  when the client has no `baseUrl` (only the owners client lacks one).

Opening registration on the owners realm with an automatic role would make anyone who
finds `/admin/` internal staff. Instead, registration creates an account with **no
access** until a human grants a role.

## 2. Keycloak — `anamnys-owners.json`

- `registrationAllowed: true`.
- `verifyEmail: true` (already set) means Keycloak finishes email verification before the
  first login completes.
- The realm's default roles must not include `owner`, `support` or `ops`. They do not
  today; this spec makes that an explicit invariant.
- The existing password policy (`length(16) and notUsername and notEmail and
  passwordHistory(10)`) and brute-force settings apply to the registration form
  unchanged.
- The realm's user profile stays the default (username/email/first/last name); the theme's
  `Register.tsx` already renders whatever the realm's profile declares.
- Dev environments must wipe the Keycloak and Postgres data volumes to pick up the realm
  change (realm import runs only once — see CLAUDE.md).

## 3. Data — `Staff.Role` becomes nullable

`Role IS NULL` means **pending**: the account exists and can sign in, but holds no staff
role and may not use any admin surface.

- `anamnys-db-script.sql`: `"Role" text` (drop `NOT NULL`). `Staff_Role_ck` stays as is;
  a PostgreSQL `CHECK` passes on `NULL`, so non-null values are still limited to `owner`,
  `support`, `ops`.
- `Staff.Role` entity property: `string?`, no default.
- `DatabaseInitializer` bootstraps only a fresh database, so existing dev databases need
  the same volume wipe as section 2 (both happen together).

No new status column: the role is the only thing that distinguishes pending from active,
and a second column could disagree with it.

## 4. Provisioning — `FirstLoginProvisioner.ProvisionStaffAsync`

On every owners-realm login:

1. Read the staff role from the token (`roles` claims, first of `owner`, `support`,
   `ops`), or `null` when none is present.
2. Existing row:
   - `DisabledAt` set → refuse (unchanged).
   - Token role differs from the stored role and the token role is not `null` → update
     `Role` and `UpdatedAt`. This is how a pending account becomes active after an
     approval, and how a role change follows the token.
   - Token role is `null` → leave the stored role alone. Authorization reads the token,
     not the row (section 5), so a revoked role already loses access; the row is not the
     source of truth for permissions.
   - Return the row id.
3. No row → insert one with `Role` = token role (possibly `null`). The existing
   insert-race handling (re-read by `ExternalSubject` on `DbUpdateException`) is kept.

A login is no longer refused for lacking a role. It is still refused for a disabled row
or a missing `sub`/`email` claim.

## 5. Authorization

- The `/api/admin` group in `Program.cs` adds a role requirement on top of the owner
  cookie: the principal must carry a `roles` claim of `owner`, `support` or `ops`.
- A pending account hitting `/api/admin/*` gets **403** (authenticated, not permitted).
  A cookie from another realm still gets **401** — the scheme list is unchanged, so the
  "wrong realm is 401, never 403" rule in CLAUDE.md holds.
- `/api/auth/me` is unchanged. A pending account gets `roles: []`, which is what the
  admin app keys off.

The role check reads the token's claims, not `Staff.Role`, so it needs no database query
and cannot drift from what Keycloak granted.

## 6. Admin app — `apps/admin`

`routes/index.tsx` stops auto-redirecting. It renders one of three states:

| State | Condition | Screen |
|---|---|---|
| Anonymous | `!user` after load, no `authError` | Card with **Entrar** (`/auth/owner/login`) and **Criar conta** (`/auth/owner/register`) |
| Pending | `user` with none of the staff roles | "Conta aguardando aprovação" message and a sign-out button |
| Active | `user` with a staff role | The existing home |

- `authError` still renders `AuthErrorNotice` first, as today.
- Links are plain full-page `<a href>` navigations, as the BFF routes require.
- Sign-out uses the existing shared `/auth/owner/logout` form POST.
- Strings go through i18next, matching the other apps.

## 7. Visibility

Nothing changes in `apps/web`, `apps/provider` or `apps/patient`. `/sign-in` and
`/get-started` keep omitting the owners realm. The Keycloak theme already hides site
navigation for the owners client. The theme's `Login.tsx` renders no "register" link for
any realm, so the admin app's landing screen is the only place that links to owner
registration.

## 8. Approving a pending account

Manual, in the Keycloak admin console: realm `anamnys-owners` → Users → the user → Role
mapping → assign `support`, `ops` or `owner`. The user signs out and back in; the new
token carries the role and section 4 activates the row. Documented in CLAUDE.md.

## 9. Testing

- Provisioner: a token without a staff role creates a pending row; a token with a role
  activates an existing pending row; a disabled row is still refused.
- `/api/admin/probe`: pending account → 403; staff account → 200; provider or patient
  cookie → 401.
- Admin app: `npm run build` and `npm run lint`.

## 10. Out of scope

- An in-app approval list and role assignment (needs the server to write roles through
  the Keycloak admin API).
- Notifying existing owners when someone registers.
- Restricting registration by email domain.
