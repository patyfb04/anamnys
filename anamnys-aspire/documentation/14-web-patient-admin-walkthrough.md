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

## `apps/patient` — explicitly a scaffold

This one is refreshingly honest about its own state. Its entire home route:

```tsx
// Scaffold only. The patient app will own booking, rescheduling and cancelling
// appointments; those routes land once the appointment API exists.
function Home() {
  return (
    <main className="mx-auto max-w-2xl px-6 py-16">
      <h1 className="text-3xl font-semibold tracking-tight text-slate-900">Anamnys</h1>
      <p className="mt-3 text-slate-600">
        Patient portal. Booking, rescheduling and cancelling appointments will live here.
      </p>
    </main>
  );
}
```

That's the whole app today: `main.tsx`, `__root.tsx`, `index.tsx`, and the usual Vite
scaffolding files — no components, no API calls, nothing wired to `useAuthStore`. The code
comment tells you exactly what's blocking it: an appointment API. Chapter 10's tour of
`Appointments`, `AppointmentSeries`, `BookingHolds`, and `BookingPolicies` shows you the
schema this app is waiting on — it exists in the database, but not yet behind any
`/api/phi/*` endpoint. If you're ever asked to start building out the patient portal, this
is the dependency to check first.

## `apps/admin` — scaffolded, but with real authentication already wired

This one sits in an interesting middle position, worth distinguishing carefully from
`apps/patient`'s pure scaffold. Its home route is genuinely functional as far as
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
yet, where `apps/patient` doesn't have plumbing at all.

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
