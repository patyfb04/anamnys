# Chapter 6: Authentication Architecture — the BFF Pattern

This is the most deliberately engineered part of the codebase, and two committed design
documents explain the reasoning in more depth than the code comments alone ever could:
`design/specs/2026-08-24-authentication-design.md` (the original design) and
`design/specs/2026-09-05-keycloak-implementation-design.md` (which extends it — notably
adding a third realm — rather than replacing it). This chapter distills both into the
concepts you need before Chapters 7 and 8 show you the actual implementation; if you ever
need the full reasoning behind a decision summarized here, those two files are the primary
source, and they're worth reading in full at some point.

## Why not just use a hosted identity provider?

The obvious modern default — Clerk, Auth0, or similar — was evaluated and rejected, and
the reasoning is worth understanding because it's the same reasoning that shows up
throughout Chapter 1's HIPAA constraints. Two problems, distinct from each other:

First, **cost and contract structure**: a hosted provider's HIPAA Business Associate
Agreement is typically an enterprise-tier, custom, annually-billed arrangement — not
something a small product can simply opt into via a pricing page checkbox.

Second, and more fundamentally: **patients authenticating means the identity provider
holds PHI, even before any clinical data is involved.** The mere association between a
named person and a mental-health practice is itself protected health information. Routing
that through a third-party IdP reintroduces exactly the kind of third-party PHI exposure
that the in-process Whisper/LLaMA decision (Chapter 1) exists specifically to avoid. And
because the deployment is self-hosted rather than running inside a major cloud, there's no
umbrella BAA (the kind AWS or Azure offer for their own managed services) that could cover
a hosted IdP anyway.

Keycloak solves this by being self-hostable: no BAA required (because there's no third
party in the loop at all), a first-party Aspire hosting integration, and residency for EU/
Brazil users solved by *where you deploy it* rather than by a vendor's contractual
promises.

## The core decision: realm separation as a structural guarantee, not a policy check

The original design specified **two** realms — providers and patients. The
implementation-design document later added a **third**, `anamnys-owners`, for the people
who run the business itself (tenancy, billing, provider account administration) with
deliberately no standing access to PHI. The reasoning behind having separate realms at all
is what matters most here, and it applies identically to all three:

> Separate realms mean separate user stores, separate token signing keys, and separate
> issuers. A patient token cannot carry `provider` — it is signed by the wrong key and
> fails validation before any role check executes.

Read that carefully, because it's the crux of the whole design. This is **not** "check a
role claim and reject if it's wrong" — that's a *policy* check, something application code
decides at request time, and application code can have bugs. This is a *structural*
guarantee: a token issued by the patients realm is cryptographically incapable of being
mistaken for a token from the providers realm, full stop, before any of *our* code runs at
all. For a HIPAA access-control argument (§164.312(a)(1)), "this class of mistake is
impossible" is a materially stronger claim than "we wrote a check for this," and it's why
the design accepts a real cost — a clinician who is also a patient of the practice needs
two separate accounts — in exchange for that guarantee.

Chapter 8 shows you exactly where this guarantee becomes visible in running code: an
owners-realm cookie presented to a provider-only endpoint doesn't get a 403 Forbidden (a
policy failure). It gets a **401 Unauthorized** — because that endpoint group never
registered the owners scheme as one it accepts, so as far as that endpoint is concerned,
there's no authenticated principal there at all. If you ever see this pattern produce a
403 instead, that's a signal something has gone wrong at the scheme-registration level,
not the policy level — this exact distinction is called out as the thing to test for in
the design's testing section.

## The three realms today

```
Keycloak
├── anamnys-providers   clinicians, clinic admins, billing staff
├── anamnys-patients    patients only
└── anamnys-owners      product owners and internal staff (support, ops)
```

Each realm has its own confidential OIDC client (`anamnys-web`, `anamnys-patient-web`,
`anamnys-admin-web` respectively) using Authorization Code + PKCE, plus a shared
bearer-only API audience (`anamnys-api`) used across all three for the token-validation
side. "Confidential" here specifically means the client holds a secret — because the .NET
*server* is the one authenticating to Keycloak, never the browser. There is no public
client, and no implicit flow anywhere in this system.

The owners realm's boundary with PHI is enforced the same structural way as the
provider/patient split: owners reach tenancy, subscriptions, billing, provider account
administration, usage counters, and audit logs — never `Notes`, `Transcripts`,
`ClinicalDocuments`, `Sessions`, `TreatmentPlans`, or patient demographics. Not because a
policy says no, but because the endpoint groups that serve those resources simply don't
list the owners scheme as one they accept. This is HIPAA's "minimum necessary" principle
(§164.502(b)) held structurally rather than promised procedurally: an owner account isn't
a workforce member with PHI access, so it doesn't carry the training, sanctioning, and
periodic access-review obligations a standing PHI grant would create.

That said, support staff sometimes genuinely need to help debug a specific patient issue.
The design's answer is **break-glass access**: a narrow, time-boxed, two-person-authorized,
fully-logged exception path, reachable only through an explicitly enumerated, read-only
`/api/admin/break-glass/*` endpoint group — never a backdoor into the normal PHI endpoints.
A `BreakGlassGrants` row requires a different staff member to authorize than the one who'll
use it (enforced by a database check constraint, not just application logic, so it can't be
bypassed by an application bug), requires a support-ticket reference, and expires quickly.
Every read made under a grant appends an `AccessLogs` entry recording who, under what
grant, and why. This table exists in the schema today (Chapter 9); the break-glass
endpoints themselves ship alongside whatever PHI feature they'd need to reach — there's no
point building read access to data that doesn't exist yet.

## The core decision: Backend-for-Frontend, not tokens in the browser

The second pillar of the design, independent of realm separation, is *how* a session is
represented once someone has logged in. The alternative most SPA developers reach for by
default — storing a JWT in `localStorage` or memory and attaching it as a bearer header —
is explicitly rejected here. Instead:

```
Browser ──HttpOnly, Secure, SameSite=Strict cookie──▶ .NET server (BFF)
                                                          │ holds access + refresh tokens
                                                          ▼
                                                     Keycloak / APIs
```

The browser never holds an access token or a refresh token in any JavaScript-reachable
form — only an opaque, `HttpOnly` session cookie that JavaScript cannot read even if it
tried. The actual OAuth tokens live entirely server-side (Chapter 8 shows you exactly
where — a Redis-backed ticket store). This has one overriding benefit: **an XSS
vulnerability in any of the four SPAs has nothing to steal.** There's no token sitting in
`localStorage` or a JS variable for a malicious script to exfiltrate. It also sidesteps a
whole category of problems that come with the alternative — no CORS configuration needed
(everything is same-origin, per Chapter 4 and 11), no silent-refresh-in-a-hidden-iframe
trick, no client-side token-expiry bookkeeping.

This design also fixed a real, pre-existing weakness worth knowing about even though it
predates the current codebase: an earlier system passed JWTs to SignalR connections as a
`?access_token=...` query string, because WebSocket upgrade requests can't carry custom
headers the way normal HTTP requests can. That approach writes an access token straight
into server access logs — a genuine PHI-adjacent exposure. Same-origin cookie auth
eliminates the problem entirely: the WebSocket upgrade just carries the ordinary session
cookie like any other same-origin request, and no token ever touches a URL or a log line.

The BFF being "same-origin" with all four SPAs is not incidental — it's precisely what
Chapter 4's `PublishWithContainerFiles` and Chapter 11's sub-path serving model exist to
guarantee. Cookie-based auth without CORS headaches only works because the browser
genuinely can't tell the SPA and the API apart as different origins.

## Identity mapping: no passwords, ever, in the application database

One more principle worth internalizing before Chapter 8 shows you the code: **the
application never stores a credential of any kind.** No password column, no hashing, no
reset tokens, no recovery codes — all of that is Keycloak's job, entirely. What the
application database *does* store, per provider/patient/staff row, is an `ExternalSubject`
— the Keycloak user's UUID — and that's the only thing tying a local database row to a
real identity. This mapping is populated by **first-login provisioning**: the first time
someone successfully authenticates, if no local row exists yet for their subject, one gets
created (or, for patients specifically, an existing pre-created row gets bound to them —
see Chapter 8 for why patients are handled differently from providers and staff).

Because this project is greenfield — no legacy accounts, no migration to reconcile — this
mapping was designed correctly from the very first commit rather than retrofitted onto an
existing password system. That's a genuine advantage worth appreciating: you won't find
any "legacy compatibility" branches in this part of the code.

With the conceptual model in place, Chapter 7 goes into how Keycloak itself is configured
and built, and Chapter 8 walks the C# that turns all of this into a working login.
