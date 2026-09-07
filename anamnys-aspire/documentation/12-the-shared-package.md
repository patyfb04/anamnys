# Chapter 12: The Shared Package (`packages/shared`)

Every one of the four SPAs — plus, partially, the Keycloak theme app — imports from
`@anamnys/shared`. This chapter walks its four areas: the API client, the UI kit, shared
state, and domain types. Understanding this package well pays off across every remaining
frontend chapter, because nearly every concrete example in them reaches into here.

## The API client (`src/api/*`)

```ts
// src/api/client.ts
const api: AxiosInstance = axios.create({
  baseURL: BASE_URL,       // "/api" — resolves through each app's same-origin Vite proxy
  timeout: 30_000,
  headers: { "Content-Type": "application/json" },
  withCredentials: true,   // makes the browser send/accept the HttpOnly session cookie
});
```

`withCredentials: true` is called out as mandatory in `CLAUDE.md`, and the reasoning
connects directly back to Chapter 6: this is what makes the browser actually attach the
`__Host-anamnys-*` cookie to same-origin requests and accept the `Set-Cookie` response —
without it, the entire BFF auth model from Part 4 simply doesn't work from the browser
side, no matter how correct the server is. There is deliberately no JS-readable token
anywhere in this client; that absence *is* the point, not an oversight.

The response interceptor normalizes every error into a plain `Error` with a `.message`,
so calling code never has to reach into an Axios-specific error shape. Around this one
file sit seven domain-specific modules — `account.ts`, `auth.ts`, `contact.ts`, `notes.ts`,
`patients.ts`, `transcribe.ts` — each a thin, typed wrapper around a handful of endpoints,
consumed by components via TanStack Query rather than called directly inline.

`CLAUDE.md` lists this file as mid-migration: **axios is being replaced with native
`fetch`**, confined entirely to this one file (no `Axios*` type escapes it — the seven
domain modules only ever touch the default exported instance). If you're asked to do this
migration, the one thing not to lose is `credentials: 'include'` — the `fetch` equivalent
of `withCredentials: true` — and the response interceptor's error-normalization behavior,
since callers throughout the app currently depend on getting a plain `Error` back. Also
worth remembering: TanStack Query is an async-state and caching layer, not an HTTP client
on its own — the replacement is Query *plus* `fetch`, not Query alone.

## The UI kit (`src/ui/*`)

A modest component library — `Button`, `Card`, `TextField`, `Checkbox`, `Badge`, `Avatar`,
`AlertMessage`, `AppTabBar`, `Sidenav`, `TopBar`, `AuthErrorNotice`, and `alert-dialog.tsx`
— shared across every app so visual consistency doesn't depend on four separate teams
reimplementing a button. You saw `TopBar`, `Card`, `Avatar`, and `Button` all imported
together in Chapter 13's look at `PatientDetailView` — that's the normal pattern: compose
a page from these primitives rather than writing raw markup per app.

One file, `alert-dialog.tsx`, is a migration boundary worth knowing about specifically:
it's the *only* place `@base-ui/react` is used anywhere in the frontend. `CLAUDE.md` names
shadcn/ui as the intended replacement, and because the dependency is confined to exactly
one file, this is a good candidate to replace opportunistically whenever you're touching
that file for another reason, rather than needing a dedicated migration effort.

## Shared client state (`src/lib/store/*`)

Two Zustand stores live here, and `useAuthStore` in particular is worth reading closely,
because it's the frontend half of everything Chapter 8 covered server-side:

```ts
login: (realm, returnUrl) => {
  const query = returnUrl ? `?returnUrl=${encodeURIComponent(returnUrl)}` : "";
  window.location.assign(`/auth/${realm}/login${query}`);
},

logout: (realm) => { authApi.logout(realm); },
```

Both are **full-page navigations**, not `fetch` calls — exactly matching the constraint
Chapter 8's `AuthEndpoints.cs` comments describe from the server side (a 302 response to
an XHR call is useless; the browser has to actually follow the redirect as a document
navigation). Notice `logout` doesn't call `set(...)` afterward to clear the store's user
state — the code comment explains why: the document is already on its way to Keycloak and
back, so there's no in-app state left to meaningfully clear, and clearing it early would
make an inherently full-page operation look, misleadingly, like it completed via XHR.

The `isLoading: true` initial value is a small but easy-to-miss detail worth understanding
if you're debugging a login flash-of-redirect bug: it defaults to `true`, not `false`,
specifically because of React effect ordering — a child route's auth-gate effect
(`!isLoading && !user`) fires *before* the ancestor layout's effect that calls
`loadUser()`, since child effects run first on initial mount. Defaulting to `false` would
make that gate briefly see "not loading, no user" and bounce a genuinely authenticated
person to the login screen for one frame, on every single page load.

`useSettingsStore` is a much simpler, lower-stakes store for the UI's language
preference — explicitly noted in its own comment as fine to keep in plain `localStorage`,
since (unlike anything in `useAuthStore`) it's a preference, never a credential.

## Domain types (`src/lib/types.ts`)

This file is worth reading end to end at some point, because it's effectively the
frontend's advance description of the clinical pipeline from Chapter 1 and the data model
from Chapter 10 — written before most of the backend that would produce this data exists.
`Note`, `StructuredNote`, `BillingCode` (with both a `confidenceScore` and a
`denialRiskScore`, matching `BillingCodes.DenialRisk` in the SQL schema exactly),
`AuditEntry`, `PipelineProgress` (`step: "transcribing" | "structuring" | "extracting" |
"billing" | "qa" | "done"`) — all of it describes a pipeline the frontend is ready to
render the moment the backend produces it.

One comment here is worth flagging as a small inconsistency to be aware of rather than
trust blindly: `SUPPORTED_LANGUAGES` (today, just `[{ code: "pt", label: "Portuguese" }]`
— consistent with the Brazil-specific detail Chapter 10 surfaced throughout the data
model) carries the comment "Keep in sync with the backend's
`ClinicalDraft.Domain.SupportedLanguages`." No `ClinicalDraft.Domain` namespace exists
anywhere in `anamnys-aspire.Server` today — the actual server namespace is
`Anamnys.Server`. This is very likely a holdover comment from the deleted `backend/`
project mentioned in Chapter 2, or from an even earlier internal naming scheme. It's a
useful reminder that comments, like code, can go stale — verify a referenced type or
namespace actually exists before trusting a comment that names one, especially in a
codebase this actively evolving.

## Locally-mocked data (`src/api-mock/`)

A small `featureFlags.ts` and `index.ts` live here, letting an app run against mock data
rather than a live backend — useful given how much of the pipeline described above doesn't
have a real server implementation yet.

## The `exports` map

`packages/shared/package.json` uses a subpath `exports` map (`./api/*`, `./ui/*`,
`./components/*`, `./lib/*`, `./lib/store/*`, `./lib/i18n`) rather than a single default
export. This is why you'll see imports shaped like `@anamnys/shared/ui/Button` or
`@anamnys/shared/api/patients` throughout every app, rather than a single umbrella import —
each subpath resolves directly to its source `.ts`/`.tsx` file, keeping the package
effectively source-shared rather than requiring a separate build step of its own.

With the shared foundation covered, Chapters 13–15 go app by app, starting with the one
that exercises this shared layer the most: `apps/provider`.
