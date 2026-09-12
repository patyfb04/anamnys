# Provider Self-Registration (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire "Comecar" to real Keycloak-hosted self-registration for the `anamnys-providers` realm, landing a freshly-registered provider on a real (placeholder) dashboard instead of the patient list.

**Architecture:** A new `/auth/{realm}/register` server endpoint issues the same OIDC challenge as `/auth/{realm}/login`, marked so `AuthenticationSetup.cs` rewrites the outbound redirect from Keycloak's `.../auth` to `.../registrations` — Keycloak's own direct-to-registration URL, same query params. A new themed `Register.tsx` page (Keycloakify) renders the form; Keycloak owns the credential entirely. `apps/web` gets a `/get-started` persona picker (Profissional live, Paciente disabled) in front of it.

**Tech Stack:** ASP.NET Core OIDC (`Microsoft.AspNetCore.Authentication.OpenIdConnect`), Keycloakify (React theme), TanStack Router (file-based), xUnit + FluentAssertions + Aspire.Hosting.Testing.

**Spec:** `anamnys-aspire/design/specs/2026-09-07-provider-patient-registration-design.md`

## Global Constraints

- Phase 1 is **provider only**. `anamnys-patients.json` and `anamnys-owners.json` stay `registrationAllowed: false` — do not touch either file.
- No `FirstLoginProvisioner` change this phase — the Providers branch (create-on-first-login) already does what's needed.
- File/route naming is English (`get-started.tsx`, `/get-started`, `appUrls.getStarted`); on-screen text is Portuguese (this repo has one locale, `pt.json`).
- The Paciente option on `/get-started` renders disabled ("Em breve") — do not link it anywhere yet.
- Every realm-JSON change requires removing the Keycloak/Postgres Docker volumes locally before it takes effect (realm import only runs when the realm doesn't already exist).
- Provider dev server: port 5273, `base: '/provider/'`. Admin: port 5276, `base: '/admin/'`. `/auth/*` and `/signin-oidc-*` are proxied at root regardless of an app's own base path.

---

### Task 1: Enable self-registration on the providers realm

**Files:**
- Modify: `anamnys-aspire/keycloak/realms/anamnys-providers.json:7`

**Interfaces:**
- Consumes: nothing.
- Produces: a realm where `GET /realms/anamnys-providers/protocol/openid-connect/registrations` renders Keycloak's registration form instead of an error — Task 2's endpoint and Task 3's theme page both depend on this being `true`, though Task 2's own tests do not (they only check which URL our server redirects to, not what Keycloak does with it).

- [ ] **Step 1: Flip `registrationAllowed` and add `registrationEmailAsUsername`**

In `anamnys-aspire/keycloak/realms/anamnys-providers.json`, change line 7 and add a new line right after it:

```json
  "registrationAllowed": true,
  "registrationEmailAsUsername": true,
```

(Line 7 today is `"registrationAllowed": false,` — flip the value and insert the new line directly below it, before `"resetPasswordAllowed": true,`.)

- [ ] **Step 2: Manual verification**

This is realm-config JSON — there's no automated test for it in this repo (realm JSON has no schema-validation tooling here). Verify by hand once Task 2 is also done:

```bash
# from anamnys-aspire/
aspire stop
docker volume ls | grep -E 'keycloak-data|postgres-data'
docker volume rm <keycloak-data>   # one at a time
docker volume rm <postgres-data>
aspire start
```

Then open `http://localhost:8080/realms/anamnys-providers/protocol/openid-connect/registrations?client_id=anamnys-web&response_type=code&scope=openid&redirect_uri=http://localhost:5273/signin-oidc-provider` in a browser — it should render a registration form (Keycloak's default theme is fine at this point; Task 3 themes it), not a "Registration not allowed" error page.

- [ ] **Step 3: Commit**

```bash
git add anamnys-aspire/keycloak/realms/anamnys-providers.json
git commit -m "feat: allow self-registration on the providers realm

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 2: `/auth/{realm}/register` server endpoint

**Files:**
- Modify: `anamnys-aspire/anamnys-aspire.Server/Auth/AuthEndpoints.cs` (inside `MapRealm`, right after the existing `/login` route)
- Modify: `anamnys-aspire/anamnys-aspire.Server/Auth/AuthenticationSetup.cs` (inside the `AddKeycloakOpenIdConnect` options block, alongside the other `options.Events.On*` handlers)
- Test: `anamnys-aspire/anamnys-aspire.Tests/Auth/RegisterEndpointTests.cs` (new)

**Interfaces:**
- Consumes: `AuthSchemes.ProviderOidc`, `SafeLocalRedirect` (both already in `AuthEndpoints.cs`); the `Wirings` loop and `RealmWiring` record in `AuthenticationSetup.cs` (unchanged shape).
- Produces: `GET /auth/{realm}/register` — a 302 whose `Location` targets `.../protocol/openid-connect/registrations` instead of `.../auth`. Task 4's `get-started.tsx` links straight to `/auth/provider/register`.

This endpoint is mounted for all three realms mechanically (same as `/login`), but only actually renders a form where the realm's `registrationAllowed` is `true` — Keycloak itself is the gate, not this code.

- [ ] **Step 1: Write the failing tests**

Create `anamnys-aspire/anamnys-aspire.Tests/Auth/RegisterEndpointTests.cs`:

```csharp
using System.Net;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// /auth/provider/register (AuthEndpoints.cs) reuses the ordinary login challenge but
// marks it so AuthenticationSetup.cs's OnRedirectToIdentityProvider sends the browser to
// Keycloak's registration form instead of its login form. This only proves our own
// redirect-building code took the right branch — it does not depend on
// anamnys-providers.json's registrationAllowed at all, so it stays green regardless of
// that realm setting.
[Collection(SharedAppHostCollection.Name)]
public class RegisterEndpointTests
{
    // Same fixed-port requirement as OwnerSessionCookieTests: the server derives its
    // redirect_uri from the Host header, and only the provider app's registered
    // redirect URI (keycloak/realms/anamnys-providers.json) is accepted.
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");

    [Fact]
    public async Task RegisterEndpoint_RedirectsToKeycloaksRegistrationsEndpoint_NotLogin()
    {
        // Arrange
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = ProviderBaseAddress };
        await WaitForProviderDevServerAsync(client, TestContext.Current.CancellationToken);

        // Act
        using var response = await client.GetAsync(
            "/auth/provider/register?returnUrl=/provider/", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location;
        location.Should().NotBeNull();
        location!.AbsolutePath.Should().Contain(
            "/protocol/openid-connect/registrations",
            $"the register endpoint must redirect to Keycloak's registration form, not its login form. Got: {location}");
        location.AbsolutePath.Should().NotContain("/protocol/openid-connect/auth");
    }

    [Fact]
    public async Task LoginEndpoint_StillRedirectsToKeycloaksLoginEndpoint_Unaffected()
    {
        // Arrange — the register endpoint's marker must not leak onto the ordinary
        // login challenge it shares an OIDC scheme with.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = ProviderBaseAddress };
        await WaitForProviderDevServerAsync(client, TestContext.Current.CancellationToken);

        // Act
        using var response = await client.GetAsync(
            "/auth/provider/login?returnUrl=/provider/", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location;
        location.Should().NotBeNull();
        location!.AbsolutePath.Should().Contain("/protocol/openid-connect/auth");
    }

    private static async Task WaitForProviderDevServerAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/provider/", timeout.Token);
                if ((int)response.StatusCode < 500)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project anamnys-aspire.Tests -- -method "*RegisterEndpointTests*"` (from `anamnys-aspire/`)
Expected: `RegisterEndpoint_RedirectsToKeycloaksRegistrationsEndpoint_NotLogin` FAILs — the endpoint doesn't exist yet, so the request 404s (not a 302), and `LoginEndpoint_StillRedirectsToKeycloaksLoginEndpoint_Unaffected` PASSes already (it's a pre-existing endpoint, included here as a regression guard, not because it's new).

- [ ] **Step 3: Add the `/register` route in `AuthEndpoints.cs`**

In `MapRealm`, directly after the existing `/login` route (the block ending `.AllowAnonymous();` right after the `Challenge`/`login` comment), add:

```csharp
        // Same full-page-navigation requirement as /login above, and the same
        // SafeLocalRedirect. The only difference is the "kc_action" marker in
        // AuthenticationProperties.Items, which AuthenticationSetup.cs's
        // OnRedirectToIdentityProvider reads to send the browser to Keycloak's
        // registration form instead of its login form. Mounted for every realm
        // mechanically, like /login; Keycloak itself refuses to render the form
        // wherever that realm's registrationAllowed is false (today: patients,
        // owners), so no realm-conditional check belongs here.
        app.MapGet($"/auth/{segment}/register", (string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties
                {
                    RedirectUri = SafeLocalRedirect(returnUrl, appPath),
                    Items = { ["kc_action"] = "register" },
                },
                [oidcScheme]))
            .AllowAnonymous();
```

- [ ] **Step 4: Add the redirect rewrite in `AuthenticationSetup.cs`**

Inside the `authentication.AddKeycloakOpenIdConnect("keycloak", wiring.Realm, wiring.OidcScheme, options => { ... })` block, add this alongside the existing `options.Events.OnTokenValidated` / `OnRemoteFailure` / `OnRedirectToIdentityProviderForSignOut` handlers:

```csharp
                // Comecar's registration entry point reuses the ordinary login
                // challenge (see AuthEndpoints.cs's /register route) with one
                // difference: Keycloak exposes its registration form at a sibling
                // URL to the login form, .../protocol/openid-connect/registrations
                // instead of .../auth, accepting the exact same query parameters
                // (client_id, redirect_uri, response_type, scope, state, nonce, PKCE
                // challenge). Rewriting IssuerAddress here — after the framework has
                // already built it from discovery, before the redirect is issued —
                // is the standard way to reach it; there is no separate "action" the
                // OIDC handler itself understands.
                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    if (context.Properties.Items.TryGetValue("kc_action", out var action) && action == "register")
                    {
                        context.ProtocolMessage.IssuerAddress = context.ProtocolMessage.IssuerAddress
                            .Replace("/protocol/openid-connect/auth", "/protocol/openid-connect/registrations");
                    }

                    return Task.CompletedTask;
                };
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project anamnys-aspire.Tests -- -method "*RegisterEndpointTests*"` (from `anamnys-aspire/`)
Expected: both tests PASS.

- [ ] **Step 6: Run the full auth test suite as a regression check**

Run: `dotnet run --project anamnys-aspire.Tests -- -method "*Anamnys.Tests.Auth*"` (from `anamnys-aspire/`)
Expected: all PASS — in particular `SchemeIsolationTests` and `OwnerSessionCookieTests`, proving the new event handler didn't disturb the other two realms' login flow.

- [ ] **Step 7: Commit**

```bash
git add anamnys-aspire/anamnys-aspire.Server/Auth/AuthEndpoints.cs \
        anamnys-aspire/anamnys-aspire.Server/Auth/AuthenticationSetup.cs \
        anamnys-aspire/anamnys-aspire.Tests/Auth/RegisterEndpointTests.cs
git commit -m "feat: add /auth/{realm}/register endpoint

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 3: Themed Register page in the Keycloak theme

**Files:**
- Create: `anamnys-aspire/apps/keycloak-theme/src/login/pages/Register.tsx`
- Modify: `anamnys-aspire/apps/keycloak-theme/src/login/KcPage.tsx`

**Interfaces:**
- Consumes: `Template` (from `../Template`, same signature `Login.tsx` uses: `kcContext`, `i18n`, `headline`, `subhead`, `children`), `Button` (`@anamnys/shared/ui/Button`), Keycloakify's `getKcClsx` (`keycloakify/login/lib/kcClsx`) and the `UserProfileFormFields`/`doMakeUserConfirmPassword` already declared in `KcPage.tsx`.
- Produces: `KcPage.tsx` renders `Register` for `kcContext.pageId === 'register.ftl'`. Nothing outside this theme depends on this page directly — it's reached only via the redirect Task 2 builds.

- [ ] **Step 1: Create `Register.tsx`**

```tsx
import { useState } from 'react';
import { getKcClsx } from 'keycloakify/login/lib/kcClsx';
import type { UserProfileFormFieldsProps } from 'keycloakify/login/UserProfileFormFieldsProps';
import type { LazyOrNot } from 'keycloakify/tools/LazyOrNot';
import Button from '@anamnys/shared/ui/Button';
import { Template } from '../Template';
import type { KcContext } from '../KcContext';
import type { I18n } from '../i18n';

type Props = {
  kcContext: Extract<KcContext, { pageId: 'register.ftl' }>;
  i18n: I18n;
  UserProfileFormFields: LazyOrNot<(props: UserProfileFormFieldsProps) => JSX.Element>;
  doMakeUserConfirmPassword: boolean;
};

// A native form post to Keycloak's url.registrationAction, not a controlled React form
// calling an API — same reasoning as Login.tsx, Keycloak owns the credential exchange.
// UserProfileFormFields (Keycloakify's own component, not hand-rolled TextFields like
// Login.tsx's fixed username/password pair) renders whatever fields this realm's
// declarative user profile actually configures and names its inputs correctly for
// Keycloak's registration action — hand-rolling field names here would risk silently
// mismatching them.
export default function Register({ kcContext, i18n, UserProfileFormFields, doMakeUserConfirmPassword }: Props) {
  const { url } = kcContext;
  const { msg, msgStr } = i18n;
  const [isFormSubmittable, setIsFormSubmittable] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const { kcClsx } = getKcClsx({ doUseDefaultCss: true, classes: {} });

  return (
    <Template kcContext={kcContext} i18n={i18n} headline={msg('registerTitle')} subhead={msgStr('doRegister')}>
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">{msg('registerTitle')}</h2>

      <form
        id="kc-register-form"
        action={url.registrationAction}
        method="post"
        onSubmit={() => setSubmitting(true)}
        className="mt-3.5"
      >
        <UserProfileFormFields
          kcContext={kcContext}
          i18n={i18n}
          kcClsx={kcClsx}
          onIsFormSubmittableValueChange={setIsFormSubmittable}
          doMakeUserConfirmPassword={doMakeUserConfirmPassword}
        />

        <Button
          type="submit"
          title={msgStr('doRegister')}
          loading={submitting}
          disabled={!isFormSubmittable}
          rounded="md"
          className="mt-4"
        />
      </form>

      <a href={url.loginUrl} className="block text-center mt-4.5 text-label-lg text-primary">
        {msg('backToLogin')}
      </a>
    </Template>
  );
}
```

- [ ] **Step 2: Wire it into `KcPage.tsx`**

Add the lazy import next to the other page imports:

```tsx
const Register = lazy(() => import('./pages/Register'));
```

Add the case, before the `default:` branch:

```tsx
          case 'register.ftl':
            return (
              <Register
                kcContext={kcContext}
                i18n={i18n}
                UserProfileFormFields={UserProfileFormFields}
                doMakeUserConfirmPassword={doMakeUserConfirmPassword}
              />
            );
```

- [ ] **Step 3: Type-check and build**

Run: `npm run build-vite -w @anamnys/keycloak-theme` (from `anamnys-aspire/`)
Expected: succeeds with no TypeScript errors. (Not `npm run build` — that also runs `keycloakify build`, which shells out to Maven; not needed to verify this task, only for the eventual full `aspire start`.)

- [ ] **Step 4: Lint**

Run: `npm run lint -w @anamnys/keycloak-theme` (from `anamnys-aspire/`)
Expected: no errors.

- [ ] **Step 5: Commit**

```bash
git add anamnys-aspire/apps/keycloak-theme/src/login/pages/Register.tsx \
        anamnys-aspire/apps/keycloak-theme/src/login/KcPage.tsx
git commit -m "feat: add themed Keycloak register page

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 4: `apps/web` — `/get-started` persona picker and CTA rewiring

**Files:**
- Create: `anamnys-aspire/apps/web/src/routes/_marketing/get-started.tsx`
- Modify: `anamnys-aspire/packages/shared/src/lib/appUrls.ts`
- Modify: `anamnys-aspire/apps/web/src/components/MarketingHeader.tsx`
- Modify: `anamnys-aspire/apps/web/src/routes/index.tsx` (2 CTAs)
- Modify: `anamnys-aspire/apps/web/src/routes/_marketing/product.tsx` (2 CTAs)
- Modify: `anamnys-aspire/packages/shared/src/lib/i18n/locales/pt.json`
- Modify (generated): `anamnys-aspire/apps/web/src/routeTree.gen.ts`

**Interfaces:**
- Consumes: `Card` (`@anamnys/shared/ui/Card`), `appUrls` (this task adds `getStarted` to it).
- Produces: `appUrls.getStarted = "/get-started"`; every "Comecar" CTA in `apps/web` now navigates there via `<Link>` instead of `<a href={appUrls.providerRegister}>`.

- [ ] **Step 1: Add i18n keys**

In `anamnys-aspire/packages/shared/src/lib/i18n/locales/pt.json`:

Under `"common"` (right after `"provider": "Profissional",`), add:

```json
    "patient": "Paciente",
```

Under `"welcome"` (as a new sibling of `"nav"`, `"headline"`, etc. — anywhere in that object), add:

```json
    "getStartedPage": {
      "title": "Como você quer começar?",
      "subtitle": "Escolha seu perfil para criar sua conta.",
      "providerBody": "Crie sua conta e comece a documentar suas sessões clínicas.",
      "patientBody": "Acesse seu portal para agendar e acompanhar seu atendimento."
    },
```

(`welcome.comingSoon: "Em breve"` already exists — reuse it for the disabled Paciente badge, don't add a duplicate.)

- [ ] **Step 2: Add `getStarted` to `appUrls.ts` and repoint `providerRegister`**

In `anamnys-aspire/packages/shared/src/lib/appUrls.ts`, change:

```ts
export const appUrls = {
  web: "/",
  provider: "/provider/",
  providerLogin: "/provider/login",
  providerRegister: "/provider/register",
  patient: "/patient/",
} as const;
```

to:

```ts
export const appUrls = {
  web: "/",
  provider: "/provider/",
  providerLogin: "/provider/login",
  providerRegister: "/auth/provider/register",
  getStarted: "/get-started",
  patient: "/patient/",
} as const;
```

(`providerLogin` stays exactly as-is — it's a pre-existing, separately-tracked issue, not part of this plan.)

- [ ] **Step 3: Create the `/get-started` route**

```tsx
import { createFileRoute } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { Stethoscope, HeartPulse } from 'lucide-react';
import { appUrls } from '@anamnys/shared/lib/appUrls';
import Card from '@anamnys/shared/ui/Card';

export const Route = createFileRoute('/_marketing/get-started')({
  component: GetStartedPage,
});

// Comecar's persona picker. Profissional links straight into Keycloak's (themed)
// registration form for the providers realm — see design/specs/2026-09-07-provider-
// patient-registration-design.md. Paciente is phase 2: its realm still has
// registrationAllowed: false, so it renders disabled rather than linking anywhere that
// would just fail.
function GetStartedPage() {
  const { t } = useTranslation();

  return (
    <div className="max-w-3xl mx-auto px-4 md:px-6 py-16 md:py-24">
      <h1 className="text-headline-lg text-onSurface mb-2 text-center">
        {t('welcome.getStartedPage.title')}
      </h1>
      <p className="text-body-lg text-onSurfaceVariant mb-10 text-center">
        {t('welcome.getStartedPage.subtitle')}
      </p>

      <div className="grid sm:grid-cols-2 gap-4">
        <a href={appUrls.providerRegister} className="block">
          <Card className="h-full hover:border-primary/50 transition-colors">
            <Stethoscope size={28} className="text-primary mb-3" />
            <p className="text-headline-sm text-onSurface mb-1">{t('common.provider')}</p>
            <p className="text-body-md text-onSurfaceVariant">{t('welcome.getStartedPage.providerBody')}</p>
          </Card>
        </a>

        <Card className="h-full opacity-60 cursor-not-allowed" aria-disabled="true">
          <div className="flex items-center justify-between mb-3">
            <HeartPulse size={28} className="text-primary" />
            <span className="text-label-sm text-onSurfaceVariant bg-surfaceContainerHigh rounded-radii-full px-2.5 py-1">
              {t('welcome.comingSoon')}
            </span>
          </div>
          <p className="text-headline-sm text-onSurface mb-1">{t('common.patient')}</p>
          <p className="text-body-md text-onSurfaceVariant">{t('welcome.getStartedPage.patientBody')}</p>
        </Card>
      </div>
    </div>
  );
}
```

- [ ] **Step 4: Rewire the CTAs**

In `anamnys-aspire/apps/web/src/components/MarketingHeader.tsx`, change:

```tsx
          <a
            href={appUrls.providerRegister}
            className="bg-primaryFixed text-onPrimaryFixedVariant rounded-radii-md px-4 py-2 text-label-lg text-[13px] hover:opacity-80 transition-opacity"
          >
            {t("welcome.getStarted")}
          </a>
```

to:

```tsx
          <Link
            to={appUrls.getStarted}
            className="bg-primaryFixed text-onPrimaryFixedVariant rounded-radii-md px-4 py-2 text-label-lg text-[13px] hover:opacity-80 transition-opacity"
          >
            {t("welcome.getStarted")}
          </Link>
```

(`Link` is already imported at the top of this file.)

In `anamnys-aspire/apps/web/src/routes/index.tsx`, change both occurrences (around lines 185 and 267) from `<a href={appUrls.providerRegister} ...>...</a>` to `<Link to={appUrls.getStarted} ...>...</Link>`, keeping each `className` and children exactly as they are.

In `anamnys-aspire/apps/web/src/routes/_marketing/product.tsx`, do the same for both occurrences (around lines 96 and 337).

- [ ] **Step 5: Regenerate the route tree**

Run: `npx vite build -w @anamnys/web` (from `anamnys-aspire/`) — or `cd apps/web && npx vite build`.
Expected: `apps/web/src/routeTree.gen.ts` picks up the new `/get-started` route (this file is committed, per this repo's own gotcha about `routeTree.gen.ts` needing to exist before `tsc -b` runs).

- [ ] **Step 6: Build and lint**

Run: `npm run build -w @anamnys/web` then `npm run lint -w @anamnys/web` (from `anamnys-aspire/`)
Expected: both succeed.

- [ ] **Step 7: Commit**

```bash
git add anamnys-aspire/apps/web/src/routes/_marketing/get-started.tsx \
        anamnys-aspire/apps/web/src/routes/index.tsx \
        anamnys-aspire/apps/web/src/routes/_marketing/product.tsx \
        anamnys-aspire/apps/web/src/components/MarketingHeader.tsx \
        anamnys-aspire/apps/web/src/routeTree.gen.ts \
        anamnys-aspire/packages/shared/src/lib/appUrls.ts \
        anamnys-aspire/packages/shared/src/lib/i18n/locales/pt.json
git commit -m "feat: add /get-started persona picker, wire Comecar to it

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 5: `apps/provider` — placeholder dashboard as the post-login landing

**Files:**
- Create: `anamnys-aspire/apps/provider/src/routes/_app/dashboard.tsx`
- Modify: `anamnys-aspire/apps/provider/src/routes/index.tsx`
- Modify: `anamnys-aspire/packages/shared/src/ui/Sidenav.tsx`
- Modify: `anamnys-aspire/packages/shared/src/lib/i18n/locales/pt.json`
- Modify (generated): `anamnys-aspire/apps/provider/src/routeTree.gen.ts`

**Interfaces:**
- Consumes: `useAuthStore` (`@anamnys/shared/lib/store/authStore`, specifically `user.name` — `AuthUser.name: string`).
- Produces: `/dashboard` route inside the `_app` layout; `index.tsx`'s `beforeLoad` redirect target changes from `/patients` to `/dashboard`; this is where Task 2's registration flow (and ordinary login) now lands a provider.

- [ ] **Step 1: Add i18n keys**

In `anamnys-aspire/packages/shared/src/lib/i18n/locales/pt.json`, add a new top-level `"dashboard"` object (alongside `"common"`, `"nav"`, `"welcome"`):

```json
  "dashboard": {
    "greeting": "Bem-vindo(a), {{name}}",
    "comingSoon": "Seu painel completo está a caminho."
  },
```

Also add, under `"nav"` (right after `"patients": "Pacientes",`):

```json
    "dashboard": "Painel",
```

- [ ] **Step 2: Create the placeholder dashboard page**

```tsx
import { createFileRoute } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useAuthStore } from '@anamnys/shared/lib/store/authStore';

export const Route = createFileRoute('/_app/dashboard')({
  component: DashboardPage,
});

// Placeholder landing page. The real dashboard (specs/UI/Dashboard/screen.png — stats
// tiles, today's schedule, recent activity) depends on features that don't exist
// server-side yet (transcription, notes, scheduling) — building that is its own design
// pass. This just gives a freshly registered or logged-in provider somewhere real to
// land instead of /patients.
function DashboardPage() {
  const { t } = useTranslation();
  const { user } = useAuthStore();

  return (
    <div className="p-4 md:p-8">
      <h1 className="text-headline-lg text-onSurface mb-2">
        {t('dashboard.greeting', { name: user?.name ?? '' })}
      </h1>
      <p className="text-body-lg text-onSurfaceVariant">{t('dashboard.comingSoon')}</p>
    </div>
  );
}
```

- [ ] **Step 3: Change the post-login redirect target**

In `anamnys-aspire/apps/provider/src/routes/index.tsx`, change:

```tsx
export const Route = createFileRoute("/")({
  beforeLoad: () => {
    throw redirect({ to: "/patients" });
  },
});
```

to:

```tsx
export const Route = createFileRoute("/")({
  beforeLoad: () => {
    throw redirect({ to: "/dashboard" });
  },
});
```

(The comment above it already says "goes straight to the dashboard" — it was inaccurate until now; leave it as-is, it's correct after this change.)

- [ ] **Step 4: Add Dashboard to the Sidenav**

In `anamnys-aspire/packages/shared/src/ui/Sidenav.tsx`, add `LayoutDashboard` to the `lucide-react` import list, and add a new unconditional entry as the **first** item in `NAV_ITEMS`:

```tsx
  { href: "/dashboard", Icon: LayoutDashboard, labelKey: "nav.dashboard", matchPrefix: "/dashboard" },
```

(placed before the existing `{ href: "/patients", ... }` entry — no other reordering).

- [ ] **Step 5: Regenerate the route tree**

Run: `npx vite build -w @anamnys/provider` (from `anamnys-aspire/`) — or `cd apps/provider && npx vite build`.
Expected: `apps/provider/src/routeTree.gen.ts` picks up the new `/dashboard` route.

- [ ] **Step 6: Build and lint**

Run: `npm run build -w @anamnys/provider` then `npm run lint -w @anamnys/provider` (from `anamnys-aspire/`)
Expected: both succeed. (Building `@anamnys/provider` also type-checks the `Sidenav.tsx`/`pt.json` changes in `packages/shared`, since it's a workspace dependency.)

- [ ] **Step 7: Commit**

```bash
git add anamnys-aspire/apps/provider/src/routes/_app/dashboard.tsx \
        anamnys-aspire/apps/provider/src/routes/index.tsx \
        anamnys-aspire/apps/provider/src/routeTree.gen.ts \
        anamnys-aspire/packages/shared/src/ui/Sidenav.tsx \
        anamnys-aspire/packages/shared/src/lib/i18n/locales/pt.json
git commit -m "feat: add placeholder provider dashboard, land post-login there

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

### Task 6: Full-repo verification and manual click-through

**Files:** none (verification only).

**Interfaces:** none — this task only runs what earlier tasks already produced.

- [ ] **Step 1: Root build and lint**

Run, from `anamnys-aspire/`:

```bash
npm run build
npm run lint
```

Expected: both succeed across every workspace (this catches any cross-workspace break Tasks 4/5's shared-package edits could cause in `apps/patient` or `apps/admin`, which weren't touched directly).

- [ ] **Step 2: Full C# test suite**

Run: `dotnet run --project anamnys-aspire.Tests` (from `anamnys-aspire/`)
Expected: all tests PASS, including Task 2's new `RegisterEndpointTests` and the pre-existing `FirstLoginProvisionerTests`, `SchemeIsolationTests`, `OwnerSessionCookieTests`, `BreakGlassGrantConstraintTests`.

- [ ] **Step 3: Manual click-through**

With Docker running and `aspire start` up (after Task 1's volume removal, so the realm re-imports with `registrationAllowed: true`):

1. Open the web app, click "Comecar" in the header — lands on `/get-started`.
2. Click the Profissional card — full-page nav to `/auth/provider/register`, then to Keycloak's (themed) registration form.
3. Fill it in with a new email, submit.
4. Expected: redirected back through `/signin-oidc-provider`, and the browser ends up on the provider app's `/dashboard`, showing "Bem-vindo(a), <name>".
5. Confirm the Sidenav shows "Painel" as the active, first item.

- [ ] **Step 4: Report results**

No commit for this task — if Steps 1–3 all pass, the plan is done; report that plainly. If anything fails, stop and fix it in the task that owns the failing file before moving on.
