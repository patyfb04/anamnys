# Chapter 8: Session Lifecycle in Code

Chapters 6 and 7 gave you the concepts and the Keycloak-side configuration. This chapter
walks through `anamnys-aspire.Server/Auth/` file by file, in the order a request actually
touches them, so you can trace a login from a browser click to a usable `CurrentUser
.LocalId()` on a downstream request.

## The vocabulary: `Realms.cs` and `AuthSchemes.cs`

Two small static classes define every constant the rest of the auth system refers to.
`Realms.cs` just names the three Keycloak realms (`anamnys-providers`, `anamnys-patients`,
`anamnys-owners`). `AuthSchemes.cs` is more interesting — it names **nine** authentication
schemes, three per realm:

```csharp
public const string ProviderCookie = "provider-cookie";
public const string ProviderOidc = "provider-oidc";
public const string ProviderBearer = "provider-bearer";
// ...and the same three for patient- and owner-
```

Only the cookie schemes are actually mounted on any endpoint group today (Chapter 5's
`/api/phi` and `/api/admin` groups). The bearer schemes are registered — meaning ASP.NET
Core knows how to validate a token against them if asked — but deliberately not attached to
any group yet. The reason is a real dependency, not caution for its own sake:
`FirstLoginProvisioner` (below) runs on the *OIDC* handler's `OnTokenValidated` event, which
means the `AnamnysClaims.LocalId` claim only ever gets minted on the cookie/OIDC path.
`CurrentUser.LocalId` — the sanctioned way to resolve a local row id — throws for a bearer
principal today, because nothing has ever stamped that claim onto one. Mounting the bearer
schemes now would buy nothing and guarantee a 500 the moment anyone tried to use one. When
the design's mobile client (the React Native app named in the original design's open
questions) ships, the bearer schemes come back into the endpoint groups *and*
`AnamnysClaims.LocalId` gets minted on `JwtBearerEvents.OnTokenValidated` in the same
change — the two have to move together.

## `AuthenticationSetup.cs`: registering nine schemes from one loop

This file is the single largest piece of the auth system, and its shape is a loop over a
small `RealmWiring` record — one iteration per realm, registering that realm's cookie, OIDC,
and bearer scheme together:

```csharp
private sealed record RealmWiring(
    string Realm, string CookieScheme, string OidcScheme, string BearerScheme,
    string CallbackPath, string SignedOutPath, string ClientId, string ClientSecretConfigKey);
```

### Where the tokens actually live

Before the loop even starts, two pieces of shared infrastructure get wired up.

**DataProtection keys go to Redis**, via a custom `RedisXmlRepository`, resolved lazily
through `AddOptions<KeyManagementOptions>().Configure<IConnectionMultiplexer>(...)` rather
than by building a second, throwaway service provider just to get an `IConnectionMultiplexer`
early — a shortcut that would create a duplicate Redis connection for no benefit. This
matters because DataProtection keys are what decrypt every stored session ticket — and a
session ticket, as you're about to see, holds Keycloak access and refresh tokens for
whichever PHI realm it belongs to. **Outside Development, the app refuses to start unless
`DataProtection:CertificateThumbprint` is configured**, throwing an explicit
`InvalidOperationException` naming exactly why: without it, DataProtection keys sit in Redis
unencrypted, and those keys are what unlock every stored ticket's tokens across all three
realms. In Development, the app accepts the framework's "no XML encryptor configured"
warning and moves on — this fail-closed-outside-dev pattern is one you'll see again with
`DevOnlyTestClientGuard` below.

### The cookie scheme

For each realm, `AddCookie` configures a hardened session cookie:

- Named `__Host-anamnys-provider` / `-patient` / `-owner` — the `__Host-` prefix is free
  hardening: it forces `Secure`, forbids setting `Domain`, and requires `Path=/`. Chapter 6
  already covered *why* these are distinguished by name at root path rather than scoped by
  path per app — a `/provider/`-scoped cookie simply wouldn't be sent to `/api/*` calls.
- `HttpOnly`, `SameSite=Strict`, `SecurePolicy = Always`.
- `SlidingExpiration = true`, `ExpireTimeSpan = TimeSpan.FromHours(10)`.
- `OnRedirectToLogin` and `OnRedirectToAccessDenied` are both overridden to return a bare
  401/403 status code instead of ASP.NET Core's cookie-auth default of redirecting to a
  login *page*. This matters because these are API clients (SPAs making `fetch` calls), not
  server-rendered pages — a redirect response to an XHR call is not useful, and a real login
  needs the full-page navigation described in `AuthEndpoints.cs` below, not an automatic
  redirect triggered by a failed API call.
- `OnValidatePrincipal` delegates straight into `TokenRefresher.ValidateAsync` — this is the
  hook that runs on essentially every authenticated request and decides whether the
  underlying Keycloak token needs refreshing before the request proceeds. See below.

### The OIDC handler

`AddKeycloakOpenIdConnect` configures the Authorization Code + PKCE flow itself:
`SignInScheme` points back at this realm's cookie scheme, `SaveTokens = true` hands the
tokens to the cookie's `AuthenticationProperties` — but, critically, the cookie itself never
actually carries them out to the browser, because the `ITicketStore` wired in below
serializes the whole ticket (tokens included) server-side in Redis, leaving only an opaque
key in the cookie the browser holds. `MapInboundClaims = false` keeps claim names as
Keycloak sends them rather than letting the framework remap them to legacy .NET claim URIs.
`RequireHttpsMetadata` is `!IsDevelopment()` — off locally (to match the `localhost` HTTP
flow from Chapter 7), on everywhere else.

Three event handlers here are worth understanding individually:

**`OnTokenValidated`** is where a login actually becomes a local identity:

```csharp
options.Events.OnTokenValidated = async context =>
{
    var provisioner = context.HttpContext.RequestServices.GetRequiredService<FirstLoginProvisioner>();
    var localId = await provisioner.ProvisionAsync(context.Principal!, wiring.Realm, context.HttpContext.RequestAborted);
    var identity = (ClaimsIdentity)context.Principal!.Identity!;
    identity.AddClaim(new Claim(AnamnysClaims.LocalId, localId.ToString()));
};
```

This stamps the local database row id onto the principal *before it's ever handed to the
cookie for signing*. Every downstream provider-scoped query reads this claim via
`CurrentUser.LocalId` (below) — never anything a client could have supplied directly. This
single line is the mechanical realization of Chapter 6's "provider-scoping is enforced at
the query level" principle: the id genuinely cannot come from anywhere except this exact
code path.

**`OnRemoteFailure`** exists because, without it, any exception thrown inside
`ProvisionAsync` above (an uninvited patient, an owners-realm token missing every
recognized staff role, a disabled account) would surface as a bare 500 ProblemDetails page
after an otherwise entirely successful Keycloak login — confusing for the user and equally
unhelpful for whoever's debugging it. This handler catches that, logs the underlying
exception (which is safe to log — provisioning error messages name a subject id at most,
never a claim value that could carry PHI), and redirects to the realm's landing path with a
generic `?authError=true` marker — deliberately carrying no exception detail in the URL
itself, because a URL can end up in browser history, a `Referer` header, or an access log.

**`OnRedirectToIdentityProviderForSignOut`** attaches `id_token_hint` to the outbound
logout request. Without this, Keycloak has no way to tie a logout request to a specific
session, and — depending on version — either prompts for confirmation or just ignores it,
meaning the `__Host-` cookie clears locally but the Keycloak SSO session survives. The next
person to click "Log in" on that same browser would then be silently re-authenticated as
the *previous* user. `AuthEndpoints.cs`'s logout route (below) is careful to read this token
value *before* signing out of the cookie scheme, because that sign-out is what removes the
ticket — tokens included — from Redis.

### The ticket store, wired in after the fact

```csharp
foreach (var wiring in Wirings)
{
    builder.Services.AddOptions<CookieAuthenticationOptions>(wiring.CookieScheme)
        .Configure<ITicketStore>((options, store) => options.SessionStore = store);
}
```

This is a second pass over the same three realms, run after the main registration loop, so
the shared `ITicketStore` singleton can pull its own dependencies from DI cleanly.

## `RedisTicketStore.cs`: what "server-side" actually means

This class implements `ITicketStore`, and it's short but worth reading directly — it's the
concrete mechanism behind every "tokens never reach the browser" claim in Chapters 6–7.

`StoreAsync` generates a random key (`auth:ticket:<guid>`), serializes the ticket with
ASP.NET Core's `TicketSerializer`, **encrypts it with a `DataProtector` scoped specifically
to `"Anamnys.TicketStore"`**, and writes the encrypted bytes into Redis with a TTL matching
the ticket's own expiry (falling back to 10 hours if none is set). Only that random key ever
goes into the cookie the browser holds — the actual serialized-and-encrypted ticket,
including the Keycloak access and refresh tokens, lives entirely in Redis.

`RetrieveAsync` is worth a specific mention for its error handling: if decryption or
deserialization fails — because of key rotation, a tampered value, or simple data
corruption — it doesn't let the exception propagate into a 500. It logs a warning and
returns `null`, which the framework treats as "no session," so the request is simply
challenged and the user logs in again. This is a recurring pattern in this file's
neighbors too: a failure in the session-management plumbing degrades to "please log in
again," never to an unhandled exception.

## `TokenRefresher.cs`: keeping the underlying Keycloak token alive

This runs inside the cookie handler's `OnValidatePrincipal` — meaning on essentially every
authenticated request. Its job: check whether the Keycloak access token backing this
session is expired or close to it, and if so, exchange the refresh token for a new pair
before the request proceeds, transparently to the browser.

One detail is worth calling out because it reflects a "fail closed" instinct that recurs
throughout this codebase: if the stored `expires_at` value is missing or unparseable, the
code does **not** assume the token is still fine and skip the refresh. It explicitly treats
that as "needs refresh right now" — because with `SlidingExpiration` turned on, silently
skipping a refresh would let the *cookie* keep renewing indefinitely against a Keycloak
token the server can no longer actually vouch for. The refresh call it then makes (and that
call's own failure handling) is what actually decides the session's fate, rather than
trusting an ambiguous state.

The refresh itself posts a `grant_type=refresh_token` request to Keycloak's token endpoint,
via the same `"keycloak"` named `HttpClient` from `AuthenticationSetup.cs` (which — recall
Chapter 5 — inherits resilience and service-discovery behavior automatically from
`AddServiceDefaults()`). Two distinct failure paths both land in the same place —
`context.RejectPrincipal()`, and in the explicit-failure case, an explicit sign-out too:
a non-success HTTP response from Keycloak (refresh token genuinely rejected), and any
exception thrown while making the call at all (`HttpRequestException`, `JsonException`,
`TaskCanceledException` — covering Keycloak being unreachable or returning something
unparseable). The comment on that second branch is worth repeating verbatim in spirit:
letting that exception propagate out of `OnValidatePrincipal` would turn *every*
authenticated request into a 500 for the entire duration of a Keycloak outage. Failing the
same way an explicit rejection does — log the user out, let them re-authenticate — is
strictly better than that.

## `FirstLoginProvisioner.cs`: three different provisioning strategies, on purpose

`AnamnysClaims.LocalId` is defined here (`"anamnys:lid"`) — the single claim name every
other file in this system reads. `ProvisionAsync` dispatches by realm to three genuinely
different strategies, and the differences are deliberate, not incidental:

**Providers: create-on-first-login.** If no `Provider` row has this Keycloak subject yet,
one is created immediately — a `Provider` row's existence is entirely defined by "someone
with this subject logged in at least once." Concurrency is handled honestly: two
simultaneous first logins for the same subject can both miss the initial lookup and both
attempt an insert, but a unique index on `ExternalSubject` lets exactly one succeed. Rather
than surface a 500 to whichever request lost that race, the code catches the
`DbUpdateException`, detaches the failed entity, re-reads by subject, and returns the
winner's id — the loser's request completes successfully with the same result.

**Staff (owners realm): role-gated creation.** Same create-on-first-login shape, but with
an extra check: the token's `roles` claim must contain at least one of `owner`, `support`,
or `ops`. Realm membership alone — successfully authenticating against the owners realm at
all — confers *no* role by itself. A token that authenticates but carries none of those
roles is treated as a misconfiguration and rejected outright, rather than silently
defaulting to some baseline role. (This exception is what `OnRemoteFailure` in
`AuthenticationSetup.cs` is there to turn into a clean redirect rather than a raw 500.)

**Patients: bind-only, never create.** This is the one that most surprises people coming
from the "just create an account on first login" mental model the other two realms use, and
the comment in the source states the reasoning plainly: *a `PatientAccount` exists only
because a provider already created it and invited that patient* — so a login with no
matching account is treated as an error, not as an invitation to create one on the spot.
The binding logic looks first for an existing row already bound to this subject; if none
exists, it looks for an **unclaimed** row (`ExternalSubject == null`) matching the token's
email — but only binds it if the token explicitly asserts `email_verified: true`. That
check is not optional politeness: binding an unclaimed account to whoever merely *presents*
a matching email would let anyone who controls an unverified address at the same identity
provider claim someone else's patient record. As with provider provisioning, a race on the
bind (two logins claiming the same row concurrently) is caught and resolved by re-reading
rather than failing the request outright.

Every one of these three paths also checks for `DisabledAt` where the entity supports it
(`Staff`, `PatientAccount`) and rejects a disabled account with an explicit exception rather
than silently provisioning around it.

## `AuthEndpoints.cs`: the actual HTTP surface

Three route shapes, mapped once per realm via a shared `MapRealm` helper:

**`GET /auth/{segment}/login`** — `AllowAnonymous`, issues `Results.Challenge(...)`, which
is a 302 redirect to Keycloak. The comment on this route is a genuinely important one to
internalize: this *must* be a full-page browser navigation, never an XHR/fetch call — the
whole point of the redirect is that the browser itself follows it to a cross-origin login
page, which a same-origin-restricted `fetch` simply cannot do usefully.

**`POST /auth/{segment}/logout`** — reads the stored `id_token` from the *cookie* scheme's
authentication result **before** doing anything else, specifically because signing out of
that cookie scheme is what removes the ticket (and the tokens inside it) from Redis. Only
after capturing that value does it call `Results.SignOut(...)` against both the cookie and
OIDC schemes together, carrying the `id_token` forward so `OnRedirectToIdentityProviderForSignOut`
can attach it as `id_token_hint` for Keycloak's RP-initiated logout. Like the login route,
this is meant to be a full-page form POST, not an XHR — Keycloak's logout endpoint is
cross-origin and returns no CORS headers, so an XHR call against it can only ever fail; the
cookie clears regardless (that part happens locally), but without the full-page navigation
completing the round trip to Keycloak, the SSO session there survives, with the same
silent-reauthentication risk described under `OnRedirectToIdentityProviderForSignOut` above.

**`GET /api/auth/me`** — the one endpoint that has to figure out *which* of the three cookie
schemes actually authenticated the current request, which turns out to be less obvious than
it sounds. The code explicitly calls out why it can't just read
`principal.Identity.AuthenticationType`: that value reflects whatever the OIDC handshake
stamped on the identity during federation, not which cookie scheme ultimately signed it in
— so it can't be used to distinguish the three realms. Instead, the handler tries
`httpContext.AuthenticateAsync(...)` against each of the three cookie schemes in turn, and
whichever one succeeds determines both the realm and which local table (`Providers`,
`Staff`, or `PatientAccounts`) to look the row up in.

The `SafeLocalRedirect` helper backing the login route's `returnUrl` parameter deserves a
specific mention as a genuinely good piece of defensive code: because `returnUrl` arrives
on an anonymous, unauthenticated GET, it is fully attacker-controlled. The function rejects
anything containing a control character (guarding against the `/\t/evil.com`-style tricks
some URL parsers normalize back into an open redirect after naive checks would have passed
them), rejects anything not starting with `/`, and rejects a second character of `/` or `\`
(both of which browsers can normalize into a scheme-relative `//evil.com` redirect). Only a
value that survives all three checks is used as-is; everything else falls back to the
realm's default landing path.

## `CurrentUser.cs`: the one sanctioned way to get a local id

```csharp
public static Guid LocalId(this ClaimsPrincipal principal) =>
    Guid.Parse(principal.FindFirstValue(AnamnysClaims.LocalId)
        ?? throw new InvalidOperationException("Principal has no local id claim."));
```

Barely twenty lines total, and arguably the single most important file in this entire
chapter to internalize, because it's the file every future PHI-touching endpoint is
expected to depend on. The comment above the class states the principle Chapter 1 already
introduced, now made completely concrete: *the id comes from the session, which came from a
token this server obtained itself — never from anything the client supplied.* When you
write a new endpoint that needs to know "which provider is making this request," the answer
is always `principal.LocalId()`, never a route parameter, a query string value, or a claim
you haven't traced back to `FirstLoginProvisioner`.

## `DevOnlyTestClientGuard.cs`: the last line of defense

This class runs once, at server startup, and only does anything outside Development. It
authenticates to Keycloak's own admin API (using the `KEYCLOAK_ADMIN_*` configuration wired
in by `AppHost.cs`, Chapter 4 — deliberately *not* the `"keycloak"` named `HttpClient` the
rest of the app uses, because that client's base address only resolves under Aspire's own
service discovery, and this gate has to work in real, non-Aspire-orchestrated deployments
too) and checks three things: that neither `anamnys-test-provider` nor `anamnys-test-owner`
exists in its realm, that neither seeded dev user (`dev.provider`, `dev.owner`) exists, and
that no client anywhere carries a `localhost` redirect URI or web origin. Any one of these
being true throws an `InvalidOperationException` that refuses to let the server start.

The class comment states plainly what this actually is: **defense in depth, not the primary
control.** By the time this code runs, Keycloak has already imported and activated whatever
was in the realm files — this check can only catch a problem after the fact, not prevent
it. The primary control is `strip-dev-seed.jq` from Chapter 7, which prevents the sensitive
content from ever reaching the built image in the first place. This guard exists for the
case where that filter itself regresses — a second, independent barrier, checked from a
completely different angle (querying the running system rather than filtering a build
input), so a single mistake in one layer doesn't silently become a production PHI exposure.

## Tying it together

A login, end to end: a browser navigates to `/auth/provider/login`, gets redirected to
Keycloak, authenticates, gets redirected back through `/signin-oidc-provider`; the OIDC
handler validates the token, `FirstLoginProvisioner` resolves or creates a `Provider` row
and stamps its id as `AnamnysClaims.LocalId` onto the principal; the cookie handler signs
that principal in, and `RedisTicketStore` serializes the whole ticket — tokens and all —
into Redis, leaving only an opaque key in the `__Host-anamnys-provider` cookie the browser
receives. Every subsequent request runs `TokenRefresher` on the way in, transparently
renewing the underlying Keycloak token when needed, and any endpoint that needs to know
"which provider is this" calls `principal.LocalId()` and trusts the answer completely —
because, by construction, there is no other way that claim could have gotten there.

Part 5 picks up from exactly that trusted id, and shows you what it's used to query.
