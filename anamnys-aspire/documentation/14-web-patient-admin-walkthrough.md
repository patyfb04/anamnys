# Chapter 14: Walkthrough — `apps/web`, `apps/patient`, `apps/admin`

Three very different apps, at three very different points of completion. Reading them in
this order — most to least built — gives a useful sense of where the product's energy has
actually gone so far, versus what Chapter 1's product description might otherwise suggest.

## `apps/web` — the public marketing site, and the most complete app in the repository

Somewhat counterintuitively for a clinical documentation product, the single most
fully-built SPA in this codebase isn't the provider app — it's the public marketing site.
Its route tree covers a genuinely complete small marketing site:

```
routes/
├── index.tsx
└── _marketing/
    ├── aboutus.tsx
    ├── contact-support.tsx
    ├── demo.tsx
    ├── faq.tsx
    ├── pricing.tsx
    ├── privacy-policy.tsx
    ├── product.tsx
    ├── solutions.tsx
    └── terms-of-service.tsx
```

A few structural choices here are worth knowing about because they'd be reasonable to
reach for elsewhere in the product too. Legal content — the privacy policy and terms of
service — lives as markdown files under `src/content/legal/`, rendered through a shared
`LegalDocument` component, rather than as hardcoded JSX. The FAQ follows the same pattern:
content in `src/content/site/faq.md`, parsed by `src/lib/faq.ts`, and rendered through
`FaqPageView`/`FaqAccordion`/`FaqAnswer`. This content-as-markdown approach means updating
legal text or an FAQ entry is a content edit, not a code change requiring a full
understanding of the component tree — a sensible choice for content that legal or support
staff might reasonably want to touch without a developer's help.

`MarketingHeader` and `MarketingFooter` wrap the marketing pages, and — per the `vite.
config.ts` comment mentioned in Chapter 11 — the header reflects session state by calling
`/api/auth/me`, even though this app never initiates a login itself; it's just capable of
showing "you're signed in" if a session cookie happens to already be present from having
used one of the other apps.

The contact form (`contact-support.tsx`) is the frontend half of the lone
non-clinical table from Chapter 10's data model tour, `ContactMessages` — about as low-
stakes as this codebase gets, and a useful contrast to keep in mind against everything
else in this handbook.

## `apps/patient` — the patient portal, early

The patient portal is real but small. Its authenticated layout (`routes/_app.tsx`) is the
same gate as the provider app's: no patient session (`/api/auth/me?realm=patient`) means a
full-page navigation to the BFF's patient login. A signed-in patient is a `PatientAccount`
(the portal login), never a provider's clinical record — see Chapter 8.

Behind the gate: **"Meus profissionais"** (the home page) lists the providers whose records
are linked to the account, each with the next session, and `/profissionais/$providerId`
shows that provider's upcoming sessions and paged history with status badges
(`GET /api/phi/patients/me/providers[/{id}/sessions]`,
`design/specs/2026-10-04-patient-portal-sessions-design.md`). Every read starts from
`Patients.AccountId`, and only schedule fields (start, end, time zone, modality, status)
leave the server — the portal never reaches the clinical record. Then the shared account
pages (*Dados pessoais*, *Acesso e segurança*).

One route lives deliberately **outside** that gate: `routes/convite.$token.tsx`, the
landing page of a provider's portal invitation
(`design/specs/2026-10-03-portal-invitation-design.md`). A provider invites from the
patient's record; the server emails a single-use link (`/patient/convite/{token}`, only the
token's SHA-256 is stored). The page immediately tries
`POST /api/phi/patients/me/invitations/accept`: a `401` means "no patient session", so it
offers *Entrar* and *Criar conta*, both returning to the same link; success links the
provider's record to the signed-in account and names the provider; failures explain
`invalid`, `email_mismatch` (the invitation went to another address — only the account
with that email can accept) or `already_linked`. The accept call itself answers "signed in as
a patient?", so the page needs no separate session check.

A second route outside the gate is `routes/confirmar.$token.tsx`, the page behind the link in
the appointment confirmation and reminder e-mails (`/patient/confirmar/{token}`,
`design/specs/2026-10-07-appointment-notifications-design.md`). It reads
`GET /api/public/appointment-confirmations/{token}` and shows the provider, date, time and
modality with a *Confirmar presença* button. Opening the link confirms nothing, because mail
scanners open links; only the button's `POST` does, and it answers `204`, or `410` when the link
no longer works (reschedule, cancellation, or the session already started), which the page
shows as *Este link não é mais válido.* The same `status` also drives *Presença confirmada*.
Both API routes need no login and are limited to 30 requests per minute per IP. The
server stores only the token's SHA-256.

A patient with a portal account does not need the e-mail: upcoming `scheduled` sessions in
the provider's session list (`SessionLine.tsx`) have a *Confirmar* button that calls
`POST /api/phi/patients/me/appointments/{id}/confirm`. It answers `404` unless the
appointment belongs to a record linked to the account, and `409` unless it is `scheduled` and
in the future. The effect is the same as the link: the appointment becomes `confirmed` and the
provider's bell gets a notice.

What the portal still waits for is a way for patients to book appointments: `AppointmentSeries`
and `BookingHolds` (Chapter 10) exist in the schema but have no write path yet. Providers create
appointments from their calendar, and `BookingPolicies` now has a write path
(`PUT /api/phi/providers/me/booking-policy`, Chapter 13), so the session lists show what the
provider has scheduled (plus seeded data in dev).

## `apps/admin` — scaffolded, but with real authentication already wired

Like `apps/patient`, this one has real authentication;
unlike it, almost nothing behind the login yet. Its home route is genuinely functional as far as
*authentication* goes:

```tsx
function AdminHome() {
  const { user, isLoading, login, loadUser } = useAuthStore();
  const authError = hasAuthError();

  useEffect(() => { void loadUser(); }, [loadUser]);
  useEffect(() => {
    if (!authError && !isLoading && !user) login("owner", window.location.pathname);
  }, [authError, isLoading, user, login]);

  if (authError) return <AuthErrorNotice />;
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

This isn't a placeholder in the same sense as `apps/patient`'s home page — it's a real
implementation of Part 4's authentication flow end to end: it triggers a full-page login
against the owners realm specifically (`login("owner", ...)`), guards against the
infinite-redirect-loop failure mode a provisioning rejection could otherwise cause (the
`!authError` check — see Chapter 8's `OnRemoteFailure` handling for the server-side half of
this same concern), and, once authenticated, genuinely displays the signed-in staff
member's email and roles pulled from a real `/api/auth/me` response. What's missing isn't
the auth wiring — it's everything an owner would actually *do* once logged in: no tenancy
management, no billing views, no provider administration, none of the surface Chapter 6
described as the owners realm's eventual reach. This app is a good illustration of the fact
that "scaffolded" isn't a single state — this one has working plumbing with no rooms built
yet.

## What this chapter should leave you with

Ranking these three by completeness inverts what a first guess at "what matters most to
this product" would suggest — the public-facing marketing site is furthest along, the
provider clinical app (Chapter 13) is a mix of real screens and clearly-marked stubs, the
admin app has real auth but no features, and the patient portal is an honest, single-page
placeholder. None of this is a red flag about the project — it's simply where development
effort has gone so far, and knowing the true state of each app before you go looking for a
feature in it will save you real time.

Chapter 15 covers the fifth and final frontend workspace, which sits slightly outside this
completeness spectrum because it's serving Keycloak rather than the application itself.
