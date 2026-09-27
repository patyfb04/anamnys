# Account Self-Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Signed-in providers and patients can edit their own profile ("Dados pessoais") and change password / set up 2FA / change email ("Acesso e segurança") from the account menu.

**Architecture:** Password, 2FA and email changes are Keycloak application-initiated actions reached through a new BFF route `GET /auth/{realm}/action/{action}` that challenges with `kc_action`. Profile fields live in the application database behind realm-specific `/api/phi/{providers|patients}/me/profile` endpoints. `FirstLoginProvisioner` syncs a changed, verified email from the token on every login.

**Tech Stack:** .NET 10 minimal APIs, EF Core 10 (PostgreSQL; InMemory in unit tests), xUnit v3 + FluentAssertions, Keycloak 26.6, React 19 + TanStack Router/Query, Tailwind 4.

**Spec:** `anamnys-aspire/design/specs/2026-09-26-account-self-service-design.md`

## Global Constraints

- All paths below are relative to `anamnys-aspire/` (the solution directory) unless they start with `anamnys-aspire/`.
- `dotnet test` does not work in this solution. Run tests with `dotnet run --project anamnys-aspire.Tests -- -method "*Pattern*"`.
- Integration tests start their own AppHost on the pinned ports (5273–5276, 8443). **Run `aspire stop` (and stop any Visual Studio debug session of the AppHost) before running them.**
- A running Aspire app locks `bin/`; an `MSB3021`/`MSB3027` build error means "stop the app first", not a broken project.
- C#: 4-space indent, file-scoped namespaces, primary constructors, always pass `CancellationToken`, records for request bodies, no XML doc comments, no exceptions for business-logic errors, EF Core directly (no repository pattern), no AutoMapper.
- Tests: class `<ClassName>Tests`, methods `<MethodName>_<Conditions>_<AssertedOutcome>` (no `Async` suffix), `// Arrange` / `// Act` / `// Assert` comments.
- TypeScript: 2-space indent. User-facing text goes in `packages/shared/src/lib/i18n/locales/pt.json` (the only locale).
- No PHI in logs, tokens or URLs: log row ids only, never an email address.
- Commit format `type: description`, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

---

## File Structure

**Server**
- Modify `anamnys-aspire.Server/Auth/AuthEndpoints.cs` — add `/auth/{segment}/action/{action}`, remove `/auth/{segment}/account`.
- Modify `anamnys-aspire.Server/Auth/AuthenticationSetup.cs` — forward `kc_action` to Keycloak.
- Modify `anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs` — email sync for returning providers and patients; gains an `ILogger`.
- Create `anamnys-aspire.Server/Profile/ProfileContracts.cs` — request/response records and their `Validate()`.
- Create `anamnys-aspire.Server/Profile/ProfileEndpoints.cs` — the four profile routes.
- Modify `anamnys-aspire.Server/Program.cs` — mount the profile routes on the `/api/phi` group.
- Modify `anamnys-aspire.Server/anamnys-aspire.Server.http` — document the new routes.
- Modify `keycloak/realms/anamnys-providers.json`, `keycloak/realms/anamnys-patients.json` — full `requiredActions` with `UPDATE_EMAIL` enabled.

**Tests**
- Create `anamnys-aspire.Tests/Auth/BrowserSession.cs` — real Keycloak login driver shared by integration tests (extracted from `OwnerSessionCookieTests`).
- Modify `anamnys-aspire.Tests/Auth/OwnerSessionCookieTests.cs` — use `BrowserSession`.
- Delete `anamnys-aspire.Tests/Auth/AccountEndpointTests.cs`.
- Create `anamnys-aspire.Tests/Auth/AuthActionEndpointTests.cs`.
- Modify `anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs` — constructor change + email sync tests.
- Create `anamnys-aspire.Tests/Profile/ProfileValidationTests.cs`.
- Create `anamnys-aspire.Tests/Profile/ProfileEndpointTests.cs`.

**Frontend**
- Create `packages/shared/src/api/profile.ts` — fetch-based profile client.
- Create `packages/shared/src/components/AccountPage.tsx` — page shell (title, subtitle, card).
- Create `packages/shared/src/components/ProfileEmailRow.tsx` — read-only email + "Alterar e-mail".
- Create `packages/shared/src/components/SecurityPage.tsx` — password / 2FA cards.
- Modify `packages/shared/src/ui/AccountMenu.tsx` — "Dados pessoais", "Acesso e segurança" as in-app links.
- Modify `packages/shared/src/lib/i18n/locales/pt.json`.
- Create `apps/provider/src/routes/_app/account/profile.tsx`, `apps/provider/src/routes/_app/account/security.tsx`.
- Create `apps/patient/src/routes/_app/account/profile.tsx`, `apps/patient/src/routes/_app/account/security.tsx`.

---

### Task 1: Extract a reusable browser-login test helper

The profile and action tests need a real provider session. `OwnerSessionCookieTests` already drives a real Keycloak login by hand; move that driver into a helper both can use. Pure test refactor: behaviour must not change.

**Files:**
- Create: `anamnys-aspire.Tests/Auth/BrowserSession.cs`
- Modify: `anamnys-aspire.Tests/Auth/OwnerSessionCookieTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class BrowserSession : IDisposable`
  - `static Task<BrowserSession> LoginAsync(Uri appBaseAddress, string appPath, string realmSegment, string username, string password, CancellationToken cancellationToken)`
  - `static Task WaitForDevServerAsync(HttpClient client, string appPath, CancellationToken cancellationToken)`
  - `HttpClient Client { get; }`, `Dictionary<string, string> Jar { get; }`
  - `Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)` — applies the jar, does **not** follow redirects, captures `Set-Cookie`.
  - `Task<HttpResponseMessage> SendFollowingRedirectsAsync(HttpRequestMessage request, CancellationToken cancellationToken)` — same, but follows every 3xx by hand (GET), capturing cookies on each hop. The jar is keyed by cookie name only and sent to every host, which is fine for these tests (the app and Keycloak cookie names do not collide).

- [ ] **Step 1: Create the helper**

Create `anamnys-aspire.Tests/Auth/BrowserSession.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// Drives a genuine Keycloak login through an app's dev-server origin (the only
// origins registered as redirect URIs) and keeps the resulting cookies. Two
// things rule out the stock HttpClient machinery, both learnt the hard way in
// OwnerSessionCookieTests:
//  1. CookieContainer applies the Secure-requires-https rule literally and drops
//     every Secure cookie on http://localhost, where browsers make an exception;
//     the OIDC correlation cookie then never comes back and the callback fails.
//  2. AllowAutoRedirect only exposes the final response of a chain, hiding the
//     Set-Cookie headers of every intermediate 302.
// So this keeps a hand-rolled jar and follows redirects itself.
internal sealed class BrowserSession : IDisposable
{
    private readonly HttpClientHandler _handler;

    private BrowserSession(Uri appBaseAddress)
    {
        _handler = new HttpClientHandler { AllowAutoRedirect = false };
        Client = new HttpClient(_handler) { BaseAddress = appBaseAddress };
    }

    public HttpClient Client { get; }

    public Dictionary<string, string> Jar { get; } = [];

    public static async Task<BrowserSession> LoginAsync(
        Uri appBaseAddress,
        string appPath,
        string realmSegment,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var session = new BrowserSession(appBaseAddress);
        try
        {
            await WaitForDevServerAsync(session.Client, appPath, cancellationToken);

            var loginPage = await session.SendFollowingRedirectsAsync(
                new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri(appBaseAddress, $"/auth/{realmSegment}/login?returnUrl={appPath}")),
                cancellationToken);
            var html = await loginPage.Content.ReadAsStringAsync(cancellationToken);
            loginPage.IsSuccessStatusCode.Should().BeTrue(
                $"the challenge/redirect/login-page chain must succeed; got {(int)loginPage.StatusCode} " +
                $"from {loginPage.RequestMessage?.RequestUri}. Body: {html}");

            // The theme renders the form client-side from an embedded kcContext
            // object, so there is no <form> to scrape: read url.loginAction from it.
            var actionMatch = Regex.Match(html, "\"loginAction\"\\s*:\\s*\"([^\"]+)\"");
            actionMatch.Success.Should().BeTrue("the Keycloak-hosted login page must embed kcContext.url.loginAction");
            var formAction = actionMatch.Groups[1].Value.Replace("\\/", "/");

            var submission = await session.SendFollowingRedirectsAsync(
                new HttpRequestMessage(HttpMethod.Post, formAction)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["username"] = username,
                        ["password"] = password,
                    }),
                },
                cancellationToken);
            var submissionBody = await submission.Content.ReadAsStringAsync(cancellationToken);
            submission.IsSuccessStatusCode.Should().BeTrue(
                $"credential submission must succeed; got {(int)submission.StatusCode} " +
                $"from {submission.RequestMessage?.RequestUri}. Body: {submissionBody}");

            // response_mode=form_post: Keycloak answers with a page whose onload
            // auto-submits a hidden form to the BFF callback. Replay it by hand.
            var callbackActionMatch = Regex.Match(submissionBody, "ACTION=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            callbackActionMatch.Success.Should().BeTrue(
                $"the form_post relay page must have a form action. Body: {submissionBody}");
            var callbackAction = WebUtility.HtmlDecode(callbackActionMatch.Groups[1].Value);

            var hiddenFields = Regex.Matches(
                    submissionBody, "NAME=\"([^\"]+)\"\\s+VALUE=\"([^\"]*)\"", RegexOptions.IgnoreCase)
                .Select(m => new KeyValuePair<string, string>(
                    WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
                .ToList();
            hiddenFields.Should().NotBeEmpty("the form_post relay page must carry the code/state/session_state fields");

            var callback = await session.SendFollowingRedirectsAsync(
                new HttpRequestMessage(HttpMethod.Post, callbackAction) { Content = new FormUrlEncodedContent(hiddenFields) },
                cancellationToken);
            var callbackBody = await callback.Content.ReadAsStringAsync(cancellationToken);
            callback.IsSuccessStatusCode.Should().BeTrue(
                $"replaying the form_post callback must succeed; got {(int)callback.StatusCode} " +
                $"from {callback.RequestMessage?.RequestUri}. Body: {callbackBody}");
            callback.RequestMessage?.RequestUri?.PathAndQuery.Should().NotContain(
                "authError", $"the callback must not fail server-side. Final URI: {callback.RequestMessage?.RequestUri}");

            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ApplyCookies(request);
        var response = await Client.SendAsync(request, cancellationToken);
        CaptureCookies(response);
        return response;
    }

    public static async Task WaitForDevServerAsync(HttpClient client, string appPath, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(appPath, timeout.Token);
                if (response.IsSuccessStatusCode)
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

    public void Dispose()
    {
        Client.Dispose();
        _handler.Dispose();
    }

    public async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        while (true)
        {
            var response = await SendAsync(request, cancellationToken);

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            {
                var location = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(request.RequestUri!, response.Headers.Location);
                response.Dispose();
                request = new HttpRequestMessage(HttpMethod.Get, location);
                continue;
            }

            return response;
        }
    }

    private void ApplyCookies(HttpRequestMessage request)
    {
        if (Jar.Count == 0)
        {
            return;
        }

        request.Headers.Remove("Cookie");
        request.Headers.Add("Cookie", string.Join("; ", Jar.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    private void CaptureCookies(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            return;
        }

        foreach (var setCookie in setCookies)
        {
            var firstSegment = setCookie.Split(';')[0];
            var equalsIndex = firstSegment.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            var name = firstSegment[..equalsIndex].Trim();
            var value = firstSegment[(equalsIndex + 1)..].Trim();
            if (setCookie.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase) || value.Length == 0)
            {
                Jar.Remove(name);
            }
            else
            {
                Jar[name] = value;
            }
        }
    }
}
```

- [ ] **Step 2: Rewrite `OwnerSessionCookieTests` on top of it**

Replace the whole of `anamnys-aspire.Tests/Auth/OwnerSessionCookieTests.cs` with:

```csharp
using System.Net;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// Asserts the session cookie carries no token material: SessionStore/RedisTicketStore
// exist precisely to keep the access and refresh tokens server-side, and nothing else
// would notice if that were silently bypassed. Drives a genuine owners-realm login (the
// seeded `dev.owner`, see keycloak/realms/anamnys-owners.json) and inspects the real
// `__Host-anamnys-owner` cookie.
[Collection(SharedAppHostCollection.Name)]
public class OwnerSessionCookieTests
{
    // The admin dev-server origin, not the server's own test port: the server builds the
    // OIDC redirect_uri from the Host header, and only http://localhost:5276 is registered
    // for anamnys-admin-web.
    private static readonly Uri AdminBaseAddress = new("http://localhost:5276/");

    [Fact]
    public async Task OwnerLogin_SessionCookie_CarriesNoTokenMaterialAndIsScopedToItsOwnScheme()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            AdminBaseAddress, "/admin/", "owner", "dev.owner@anamnys.local", "SupportStaff!2026Dev",
            TestContext.Current.CancellationToken);

        // Act
        var hasCookie = session.Jar.TryGetValue("__Host-anamnys-owner", out var sessionCookieValue);

        // Assert — the session cookie exists and holds an opaque reference, never the tokens.
        hasCookie.Should().BeTrue(
            $"a successful owner login must set the session cookie. Jar contents: {string.Join(", ", session.Jar.Keys)}");
        sessionCookieValue.Should().NotContain(
            "eyJ", "the cookie must hold an opaque ticket reference, never a JWT — SessionStore/ITicketStore " +
            "keep the actual access and refresh tokens server-side");
        sessionCookieValue!.Length.Should().BeLessThan(
            500, "a few hundred bytes at most: the cookie is a ticket-store key, not the serialized ticket itself");

        // Positive control: without it, a 401 from either replay below proves nothing.
        using var adminProbeResponse = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/admin/probe"), TestContext.Current.CancellationToken);
        adminProbeResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, "the owner session cookie must authenticate on the owners-only group");

        // Cookie-scheme isolation: the owners ticket replayed under the providers cookie
        // name must not authenticate on PHI (401, never 200 or 403). This rests on the
        // cookie handler's data protector including the scheme name in its purpose chain.
        using var replayedAsProvider = new HttpRequestMessage(HttpMethod.Get, "/api/phi/probe");
        replayedAsProvider.Headers.Add("Cookie", $"__Host-anamnys-provider={sessionCookieValue}");
        using var replayResponse = await session.Client.SendAsync(replayedAsProvider, TestContext.Current.CancellationToken);
        replayResponse.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "an owners-realm ticket replayed under the providers cookie name must not authenticate on PHI");

        // And the untouched owners cookie is not a PHI credential either: the owners realm
        // is absent from the /api/phi group on purpose, so this is 401, not 403.
        using var ownerOnPhiResponse = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/phi/probe"), TestContext.Current.CancellationToken);
        ownerOnPhiResponse.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized, "an owners cookie is not an authenticated principal on the PHI group at all");
    }
}
```

- [ ] **Step 3: Run the refactored test**

Run `aspire stop` first, then:
`dotnet run --project anamnys-aspire.Tests -- -method "*OwnerLogin_SessionCookie*"`
Expected: `Total: 1, Errors: 0, Failed: 0`.

- [ ] **Step 4: Commit**

```bash
git add anamnys-aspire/anamnys-aspire.Tests/Auth/BrowserSession.cs anamnys-aspire/anamnys-aspire.Tests/Auth/OwnerSessionCookieTests.cs
git commit -m "test: extract the Keycloak browser-login driver into BrowserSession"
```

---

### Task 2: `/auth/{realm}/action/{action}` and `UPDATE_EMAIL` in the realms

**Files:**
- Modify: `anamnys-aspire.Server/Auth/AuthEndpoints.cs`
- Modify: `anamnys-aspire.Server/Auth/AuthenticationSetup.cs:269-277` (`OnRedirectToIdentityProvider`)
- Modify: `anamnys-aspire.Server/anamnys-aspire.Server.http`
- Modify: `keycloak/realms/anamnys-providers.json`, `keycloak/realms/anamnys-patients.json`
- Delete: `anamnys-aspire.Tests/Auth/AccountEndpointTests.cs`
- Test: `anamnys-aspire.Tests/Auth/AuthActionEndpointTests.cs`

**Interfaces:**
- Consumes: `BrowserSession` (Task 1).
- Produces: `GET /auth/{provider|patient|owner}/action/{UPDATE_PASSWORD|CONFIGURE_TOTP|UPDATE_EMAIL}?returnUrl=<local path>` → `302` to Keycloak's authorization endpoint with `kc_action=<action>` in the authorization request; `400` for any other action; `401` without that realm's session.

**Note on PAR:** the OIDC handler uses Pushed Authorization Requests (Keycloak advertises them), so the `302` Location carries only `client_id` and a `request_uri`; `kc_action` travels in the pushed request, not the URL. The test therefore follows the redirect into Keycloak and asserts which page Keycloak renders (the `pageId` in the embedded `kcContext`): `login-update-password`, `login-config-totp`, `update-email`.

- [ ] **Step 1: Write the failing tests**

Create `anamnys-aspire.Tests/Auth/AuthActionEndpointTests.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// /auth/{realm}/action/{action} (AuthEndpoints.cs) starts a Keycloak
// application-initiated action (password, 2FA, email) for the signed-in user.
[Collection(SharedAppHostCollection.Name)]
public class AuthActionEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");

    [Theory]
    [InlineData("UPDATE_PASSWORD", "login-update-password")]
    [InlineData("CONFIGURE_TOTP", "login-config-totp")]
    [InlineData("UPDATE_EMAIL", "update-email")]
    public async Task ActionEndpoint_WithSessionAndAllowedAction_OpensThatKeycloakActionPage(string action, string expectedPageId)
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026",
            TestContext.Current.CancellationToken);

        // Act — the BFF's 302 goes to Keycloak's authorization endpoint (kc_action rides in
        // the pushed authorization request, so it is not visible in the URL); following it
        // with the live SSO session lands on the page for that action.
        using var redirect = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/auth/provider/action/{action}?returnUrl=/provider/account/security"),
            TestContext.Current.CancellationToken);
        redirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        redirect.Headers.Location!.AbsolutePath.Should().Be("/realms/anamnys-providers/protocol/openid-connect/auth");
        using var page = await session.SendFollowingRedirectsAsync(
            new HttpRequestMessage(HttpMethod.Get, redirect.Headers.Location), TestContext.Current.CancellationToken);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        var pageId = Regex.Match(html, "\"pageId\"\\s*:\\s*\"([^\"]+)\"");
        pageId.Success.Should().BeTrue($"Keycloak must render a themed page with a kcContext. Body: {html}");
        pageId.Groups[1].Value.Should().Be(expectedPageId);
    }

    [Fact]
    public async Task ActionEndpoint_WithSessionAndUnknownAction_Returns400()
    {
        // Arrange
        using var session = await BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026",
            TestContext.Current.CancellationToken);

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/auth/provider/action/delete_account"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ActionEndpoint_WithoutSession_Returns401()
    {
        // Arrange
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", TestContext.Current.CancellationToken);

        // Act
        using var response = await client.GetAsync(
            "/auth/provider/action/UPDATE_PASSWORD", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
```

Delete the obsolete test: `git rm anamnys-aspire.Tests/Auth/AccountEndpointTests.cs`.

- [ ] **Step 2: Run the tests to verify they fail**

`aspire stop`, then `dotnet run --project anamnys-aspire.Tests -- -method "*ActionEndpoint*"`
Expected: FAIL — the route does not exist, so the allowed-action and unknown-action tests get `404` and the no-session test gets `404` instead of `401`.

- [ ] **Step 3: Add the route and remove `/account`**

In `anamnys-aspire.Server/Auth/AuthEndpoints.cs`, add at the top of the `AuthEndpoints` class:

```csharp
    // Forwarded verbatim to Keycloak as kc_action, so only actions this app
    // deliberately offers get through.
    private static readonly string[] AllowedActions = ["UPDATE_PASSWORD", "CONFIGURE_TOTP", "UPDATE_EMAIL"];
```

Replace the whole `/auth/{segment}/account` block (the comment starting `// Keycloak owns passwords and 2FA, so account management is its own account` down to its `.AllowAnonymous();`) with:

```csharp
        // Keycloak owns passwords, 2FA and the login email, so changing them is a
        // Keycloak application-initiated action: the same challenge as /login plus a
        // kc_action marker that AuthenticationSetup.cs's OnRedirectToIdentityProvider
        // forwards. Keycloak runs the action against the live SSO session (themed by
        // apps/keycloak-theme), then completes an ordinary code flow back here, which
        // re-signs the cookie with fresh tokens. Requires this realm's session: the
        // action belongs to whoever is signed in, never to an anonymous caller.
        app.MapGet($"/auth/{segment}/action/{{action}}", (string action, string? returnUrl) =>
            AllowedActions.Contains(action, StringComparer.Ordinal)
                ? Results.Challenge(
                    new AuthenticationProperties
                    {
                        RedirectUri = SafeLocalRedirect(returnUrl, appPath),
                        Items = { ["kc_action"] = action },
                    },
                    [oidcScheme])
                : Results.BadRequest())
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(cookieScheme)
                .RequireAuthenticatedUser());
```

Remove the now-unused `string realm,` parameter from `MapRealm` and the `Realms.*` argument from its three call sites, restoring:

```csharp
        MapRealm(app, "provider", AuthSchemes.ProviderOidc, AuthSchemes.ProviderCookie, "/provider/");
        MapRealm(app, "patient", AuthSchemes.PatientOidc, AuthSchemes.PatientCookie, "/patient/");
        MapRealm(app, "owner", AuthSchemes.OwnerOidc, AuthSchemes.OwnerCookie, "/admin/");
```

- [ ] **Step 4: Forward `kc_action` to Keycloak**

In `anamnys-aspire.Server/Auth/AuthenticationSetup.cs`, replace the body of `options.Events.OnRedirectToIdentityProvider` with:

```csharp
                options.Events.OnRedirectToIdentityProvider = context =>
                {
                    if (context.Properties.Items.TryGetValue("kc_action", out var action) && action is not null)
                    {
                        if (action == "register")
                        {
                            context.ProtocolMessage.IssuerAddress = context.ProtocolMessage.IssuerAddress
                                .Replace("/protocol/openid-connect/auth", "/protocol/openid-connect/registrations");
                        }
                        else
                        {
                            // Application-initiated action (AuthEndpoints.cs's /action route,
                            // which allowlists the value). Keycloak reads it straight off the
                            // authorization request.
                            context.ProtocolMessage.SetParameter("kc_action", action);
                        }
                    }

                    return Task.CompletedTask;
                };
```

Also extend the comment above it: after the sentence ending `there is no separate "action" the OIDC handler itself understands.` add the line `// Any other kc_action value is an application-initiated action and is passed through as an authorization request parameter instead (the handler includes it in the pushed authorization request).`

- [ ] **Step 5: Enable `UPDATE_EMAIL` in the realm JSON**

An import that lists `requiredActions` installs only the listed ones, so export the full default list from a running Keycloak and flip `UPDATE_EMAIL`. Start the app (`aspire start`), wait for Keycloak, then from `anamnys-aspire/` export the list into the scratch file `required-actions.json` (gitignored nowhere, so delete it afterwards):

```bash
KC=$(docker ps --format '{{.Names}}' | grep '^keycloak' | head -1)
MSYS_NO_PATHCONV=1 docker exec "$KC" bash -c '
K=/opt/keycloak/bin/kcadm.sh
$K config credentials --server http://localhost:8080 --realm master --user "$KC_BOOTSTRAP_ADMIN_USERNAME" --password "$KC_BOOTSTRAP_ADMIN_PASSWORD" >/dev/null
$K get authentication/required-actions -r anamnys-providers' > required-actions.json
```

Insert it into both realm files as text, right after the `"duplicateEmailsAllowed": false,` line, so the rest of each file's formatting is untouched:

```bash
node -e '
const fs = require("fs");
const actions = JSON.parse(fs.readFileSync("required-actions.json", "utf8"))
  .map(a => a.alias === "UPDATE_EMAIL" ? { ...a, enabled: true } : a);
const block = "  \"requiredActions\": " + JSON.stringify(actions, null, 2).replace(/\n/g, "\n  ") + ",\n";
for (const f of ["keycloak/realms/anamnys-providers.json", "keycloak/realms/anamnys-patients.json"]) {
  const text = fs.readFileSync(f, "utf8");
  const anchor = /^  "duplicateEmailsAllowed": false,\r?\n/m;
  if (!anchor.test(text)) throw new Error("anchor not found in " + f);
  if (text.includes("\"requiredActions\"")) throw new Error("requiredActions already present in " + f);
  fs.writeFileSync(f, text.replace(anchor, m => m + block));
  JSON.parse(fs.readFileSync(f, "utf8"));
}'
rm required-actions.json
```

Verify both files: `docker run --rm -i ghcr.io/jqlang/jq -c '[.requiredActions[] | {alias, enabled}]' < keycloak/realms/anamnys-providers.json`
Expected: 14 entries, `UPDATE_EMAIL` with `"enabled":true`, `CONFIGURE_TOTP` and `UPDATE_PASSWORD` enabled.

Then enable it on the running dev Keycloak (realm import will not re-run on the existing volume):

```bash
MSYS_NO_PATHCONV=1 docker exec "$KC" bash -c '
K=/opt/keycloak/bin/kcadm.sh
$K config credentials --server http://localhost:8080 --realm master --user "$KC_BOOTSTRAP_ADMIN_USERNAME" --password "$KC_BOOTSTRAP_ADMIN_PASSWORD" >/dev/null
for r in anamnys-providers anamnys-patients; do $K update authentication/required-actions/UPDATE_EMAIL -r $r -s enabled=true; done'
```

Run `aspire stop` again before the tests.

- [ ] **Step 6: Document the route**

In `anamnys-aspire.Server/anamnys-aspire.Server.http`, replace the three `### Keycloak account console ...` entries with:

```
### Keycloak application-initiated action (password, 2FA, email) — needs that realm's
### session cookie, so open in a signed-in browser. 302 to Keycloak with kc_action;
### 400 for an action outside UPDATE_PASSWORD, CONFIGURE_TOTP, UPDATE_EMAIL.
GET {{Server_HostAddress}}/auth/provider/action/UPDATE_PASSWORD?returnUrl=/provider/account/security

###

GET {{Server_HostAddress}}/auth/patient/action/CONFIGURE_TOTP?returnUrl=/patient/account/security

###
```

- [ ] **Step 7: Run the tests to verify they pass**

`dotnet run --project anamnys-aspire.Tests -- -method "*ActionEndpoint*" -method "*RegisterEndpoint*" -method "*LoginEndpoint*"`
Expected: `Total: 7, Errors: 0, Failed: 0` (5 action + 2 register/login, which prove the `register` branch still works).

- [ ] **Step 8: Commit**

```bash
git add anamnys-aspire/anamnys-aspire.Server anamnys-aspire/anamnys-aspire.Tests/Auth anamnys-aspire/keycloak/realms
git commit -m "feat: start Keycloak password, 2FA and email actions from the BFF"
```

---

### Task 3: Sync a changed, verified email on login

**Files:**
- Modify: `anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs`
- Test: `anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs`

**Interfaces:**
- Produces: `FirstLoginProvisioner(AnamnysDbContext db, ILogger<FirstLoginProvisioner> logger)` (DI registration `AddScoped<FirstLoginProvisioner>()` is unchanged and resolves the logger itself).

- [ ] **Step 1: Update existing test construction**

Every `new FirstLoginProvisioner(db)` in `FirstLoginProvisionerTests.cs` becomes `new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance)`:

```bash
sed -i 's/new FirstLoginProvisioner(db)/new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance)/g' anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs
```

and add `using Microsoft.Extensions.Logging.Abstractions;` to its usings.

- [ ] **Step 2: Write the failing tests**

Append to `FirstLoginProvisionerTests`:

```csharp
    [Fact]
    public async Task ProvisionAsync_WhenReturningProviderHasNewVerifiedEmail_UpdatesStoredEmail()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "old@example.com", "A Clinician"), Realms.Providers, TestContext.Current.CancellationToken);

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "new@example.com", "A Clinician"), Realms.Providers, TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Providers.SingleAsync(TestContext.Current.CancellationToken);
        row.Email.Should().Be("new@example.com");
    }

    [Fact]
    public async Task ProvisionAsync_WhenReturningProviderEmailIsUnverified_KeepsStoredEmail()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "old@example.com", "A Clinician"), Realms.Providers, TestContext.Current.CancellationToken);

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "new@example.com", "A Clinician", emailVerified: false),
            Realms.Providers,
            TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Providers.SingleAsync(TestContext.Current.CancellationToken);
        row.Email.Should().Be("old@example.com");
    }

    [Fact]
    public async Task ProvisionAsync_WhenReturningPatientHasNewVerifiedEmail_UpdatesStoredEmail()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "old@example.com", "A Patient"), Realms.Patients, TestContext.Current.CancellationToken);

        // Act
        await provisioner.ProvisionAsync(
            PrincipalFor(subject, "new@example.com", "A Patient"), Realms.Patients, TestContext.Current.CancellationToken);

        // Assert
        var row = await db.Patients.SingleAsync(TestContext.Current.CancellationToken);
        row.Email.Should().Be("new@example.com");
    }

    [Fact]
    public async Task ProvisionAsync_WhenNewPatientEmailBelongsToUnclaimedRow_KeepsStoredEmailAndStillLogsIn()
    {
        // Arrange
        await using var db = NewContext();
        var provisioner = new FirstLoginProvisioner(db, NullLogger<FirstLoginProvisioner>.Instance);
        var subject = Guid.NewGuid();
        var localId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "old@example.com", "A Patient"), Realms.Patients, TestContext.Current.CancellationToken);
        db.Patients.Add(new Patient
        {
            Id = Guid.NewGuid(),
            FirstName = "Invited",
            LastName = "Patient",
            Email = "Taken@Example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        var secondLoginId = await provisioner.ProvisionAsync(
            PrincipalFor(subject, "taken@example.com", "A Patient"), Realms.Patients, TestContext.Current.CancellationToken);

        // Assert
        secondLoginId.Should().Be(localId);
        var row = await db.Patients.SingleAsync(p => p.Id == localId, TestContext.Current.CancellationToken);
        row.Email.Should().Be("old@example.com");
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

`dotnet run --project anamnys-aspire.Tests -- -method "*FirstLoginProvisionerTests*"` (unit tests; the app may stay running only if you build to a separate `OutDir` — simplest is `aspire stop` first).
Expected: compile error on the two-argument constructor.

- [ ] **Step 4: Implement the sync**

In `anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs`:

Change the class declaration to:

```csharp
public sealed class FirstLoginProvisioner(AnamnysDbContext db, ILogger<FirstLoginProvisioner> logger)
```

In `ProvisionProviderAsync`, replace

```csharp
        if (existing is not null)
        {
            return existing.Id;
        }
```

with

```csharp
        if (existing is not null)
        {
            await SyncProviderEmailAsync(existing, principal, email, cancellationToken);
            return existing.Id;
        }
```

In `ResolvePatientAsync`, replace

```csharp
        if (bySubject is not null)
        {
            return bySubject.DisabledAt is null
                ? bySubject.Id
                : throw new InvalidOperationException("This patient account is disabled.");
        }
```

with

```csharp
        if (bySubject is not null)
        {
            if (bySubject.DisabledAt is not null)
            {
                throw new InvalidOperationException("This patient account is disabled.");
            }

            await SyncPatientEmailAsync(bySubject, principal, email, cancellationToken);
            return bySubject.Id;
        }
```

and replace

```csharp
        var emailVerified = string.Equals(
            principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        if (!emailVerified)
```

with

```csharp
        if (!IsEmailVerified(principal))
```

Add these members at the end of the class:

```csharp
    // Keycloak owns the login email; it changes through its UPDATE_EMAIL action, which
    // only applies the new address once the user confirms it. A returning user's row
    // follows it here. A clash with another row (in practice an unclaimed patient invite
    // holding that address) keeps the old value: the login itself must never fail over
    // this. Logged by row id only — an email address is PHI.
    private async Task SyncProviderEmailAsync(
        Provider row, ClaimsPrincipal principal, string email, CancellationToken cancellationToken)
    {
        if (!NeedsEmailSync(row.Email, principal, email))
        {
            return;
        }

        var normalized = email.Trim().ToUpperInvariant();
        if (await db.Providers.AnyAsync(p => p.Id != row.Id && p.Email.ToUpper() == normalized, cancellationToken))
        {
            logger.LogWarning("Email sync skipped for provider {ProviderId}: another provider holds that email.", row.Id);
            return;
        }

        row.Email = email;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveEmailSyncAsync(row, "provider", row.Id, cancellationToken);
    }

    private async Task SyncPatientEmailAsync(
        Patient row, ClaimsPrincipal principal, string email, CancellationToken cancellationToken)
    {
        if (!NeedsEmailSync(row.Email, principal, email))
        {
            return;
        }

        var normalized = email.Trim().ToUpperInvariant();
        if (await db.Patients.AnyAsync(
                p => p.Id != row.Id && p.Email != null && p.Email.ToUpper() == normalized, cancellationToken))
        {
            logger.LogWarning("Email sync skipped for patient {PatientId}: another patient row holds that email.", row.Id);
            return;
        }

        row.Email = email;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveEmailSyncAsync(row, "patient", row.Id, cancellationToken);
    }

    private async Task SaveEmailSyncAsync(object row, string kind, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent write took the address between the check above and this save.
            await db.Entry(row).ReloadAsync(cancellationToken);
            logger.LogWarning("Email sync skipped for {Kind} {Id}: the email was taken concurrently.", kind, id);
        }
    }

    private static bool NeedsEmailSync(string? stored, ClaimsPrincipal principal, string email) =>
        IsEmailVerified(principal) && !string.Equals(stored, email, StringComparison.OrdinalIgnoreCase);

    private static bool IsEmailVerified(ClaimsPrincipal principal) =>
        string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
```

Keep the existing comment block above the old `emailVerified` check (it explains why binding requires verification); it now sits above `if (!IsEmailVerified(principal))`.

- [ ] **Step 5: Run the tests to verify they pass**

`dotnet run --project anamnys-aspire.Tests -- -method "*FirstLoginProvisionerTests*"`
Expected: all `FirstLoginProvisionerTests` pass (the 14 existing + 4 new).

- [ ] **Step 6: Commit**

```bash
git add anamnys-aspire/anamnys-aspire.Server/Auth/FirstLoginProvisioner.cs anamnys-aspire/anamnys-aspire.Tests/Auth/FirstLoginProvisionerTests.cs
git commit -m "feat: sync a changed, verified login email to the local row"
```

---

### Task 4: Profile contracts and validation

**Files:**
- Create: `anamnys-aspire.Server/Profile/ProfileContracts.cs`
- Test: `anamnys-aspire.Tests/Profile/ProfileValidationTests.cs`

**Interfaces:**
- Produces (namespace `Anamnys.Server.Profile`):
  - `sealed record ProviderProfileResponse(string Email, string Name, string? CrpNumber, string? CrpRegion)`
  - `sealed record UpdateProviderProfileRequest(string? Name, string? CrpNumber, string? CrpRegion)` with `Dictionary<string, string[]> Validate()`
  - `sealed record PatientProfileResponse(string? Email, string FirstName, string LastName, string? Phone, DateOnly? DateOfBirth)`
  - `sealed record UpdatePatientProfileRequest(string? FirstName, string? LastName, string? Phone, DateOnly? DateOfBirth)` with `Dictionary<string, string[]> Validate(DateOnly today)`
  - `static class ProfileText` with `string? Clean(string? value)` (trim; empty → null)
  - Error dictionary keys are the camelCase JSON field names: `name`, `crpNumber`, `crpRegion`, `firstName`, `lastName`, `phone`, `dateOfBirth`.

- [ ] **Step 1: Write the failing tests**

Create `anamnys-aspire.Tests/Profile/ProfileValidationTests.cs`:

```csharp
using Anamnys.Server.Profile;
using FluentAssertions;

namespace Anamnys.Tests.Profile;

public class ProfileValidationTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);

    [Fact]
    public void Validate_WhenProviderRequestIsComplete_ReturnsNoErrors()
    {
        // Arrange
        var request = new UpdateProviderProfileRequest("Ana Souza", "06/12345", "SP");

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenProviderHasNoCrp_ReturnsNoErrors()
    {
        // Arrange
        var request = new UpdateProviderProfileRequest("Ana Souza", null, " ");

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Validate_WhenProviderNameIsBlank_ReportsName(string? name)
    {
        // Arrange
        var request = new UpdateProviderProfileRequest(name, null, null);

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey("name");
    }

    [Fact]
    public void Validate_WhenProviderNameIsTooLong_ReportsName()
    {
        // Arrange
        var request = new UpdateProviderProfileRequest(new string('a', 256), null, null);

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey("name");
    }

    [Fact]
    public void Validate_WhenOnlyCrpNumberIsSet_ReportsCrpRegion()
    {
        // Arrange
        var request = new UpdateProviderProfileRequest("Ana Souza", "06/12345", null);

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKey("crpRegion");
    }

    [Fact]
    public void Validate_WhenCrpFieldsAreTooLong_ReportsBoth()
    {
        // Arrange
        var request = new UpdateProviderProfileRequest("Ana Souza", new string('1', 21), new string('S', 11));

        // Act
        var errors = request.Validate();

        // Assert
        errors.Should().ContainKeys("crpNumber", "crpRegion");
    }

    [Fact]
    public void Validate_WhenPatientRequestIsComplete_ReturnsNoErrors()
    {
        // Arrange
        var request = new UpdatePatientProfileRequest("Ana", "Souza", "+55 (11) 91234-5678", new DateOnly(1990, 1, 2));

        // Act
        var errors = request.Validate(Today);

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenPatientNamesAreBlank_ReportsBoth()
    {
        // Arrange
        var request = new UpdatePatientProfileRequest(" ", null, null, null);

        // Act
        var errors = request.Validate(Today);

        // Assert
        errors.Should().ContainKeys("firstName", "lastName");
    }

    [Theory]
    [InlineData("11 9abc-1234")]
    [InlineData("1234567890123456789012345678901")]
    public void Validate_WhenPatientPhoneIsInvalid_ReportsPhone(string phone)
    {
        // Arrange
        var request = new UpdatePatientProfileRequest("Ana", "Souza", phone, null);

        // Act
        var errors = request.Validate(Today);

        // Assert
        errors.Should().ContainKey("phone");
    }

    [Fact]
    public void Validate_WhenPatientBirthDateIsInTheFuture_ReportsDateOfBirth()
    {
        // Arrange
        var request = new UpdatePatientProfileRequest("Ana", "Souza", null, Today.AddDays(1));

        // Act
        var errors = request.Validate(Today);

        // Assert
        errors.Should().ContainKey("dateOfBirth");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

`dotnet run --project anamnys-aspire.Tests -- -method "*ProfileValidationTests*"`
Expected: compile error — `Anamnys.Server.Profile` does not exist.

- [ ] **Step 3: Implement the contracts**

Create `anamnys-aspire.Server/Profile/ProfileContracts.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Anamnys.Server.Profile;

public sealed record ProviderProfileResponse(string Email, string Name, string? CrpNumber, string? CrpRegion);

public sealed record PatientProfileResponse(
    string? Email, string FirstName, string LastName, string? Phone, DateOnly? DateOfBirth);

// Email is deliberately absent from both update requests: it is the Keycloak login and
// changes only through the UPDATE_EMAIL action (see the account self-service spec §4).
// Limits mirror the realms' declarative user profile config where one exists there.
public sealed record UpdateProviderProfileRequest(string? Name, string? CrpNumber, string? CrpRegion)
{
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        ProfileText.RequireName(errors, "name", Name);

        var crpNumber = ProfileText.Clean(CrpNumber);
        var crpRegion = ProfileText.Clean(CrpRegion);
        if (crpNumber is not null && crpNumber.Length > 20)
        {
            errors["crpNumber"] = ["O número do CRP pode ter no máximo 20 caracteres."];
        }
        if (crpRegion is not null && crpRegion.Length > 10)
        {
            errors["crpRegion"] = ["A região do CRP pode ter no máximo 10 caracteres."];
        }

        // Providers_Crp_ck: both set or both null.
        if (crpNumber is not null && crpRegion is null)
        {
            errors.TryAdd("crpRegion", ["Informe a região do CRP."]);
        }
        if (crpRegion is not null && crpNumber is null)
        {
            errors.TryAdd("crpNumber", ["Informe o número do CRP."]);
        }

        return errors;
    }
}

public sealed record UpdatePatientProfileRequest(
    string? FirstName, string? LastName, string? Phone, DateOnly? DateOfBirth)
{
    public Dictionary<string, string[]> Validate(DateOnly today)
    {
        var errors = new Dictionary<string, string[]>();
        ProfileText.RequireName(errors, "firstName", FirstName);
        ProfileText.RequireName(errors, "lastName", LastName);

        var phone = ProfileText.Clean(Phone);
        if (phone is not null && (phone.Length > 30 || !ProfileText.PhonePattern().IsMatch(phone)))
        {
            errors["phone"] = ["Use até 30 caracteres: números, espaços e + ( ) -."];
        }

        if (DateOfBirth is { } dateOfBirth && dateOfBirth > today)
        {
            errors["dateOfBirth"] = ["A data de nascimento não pode estar no futuro."];
        }

        return errors;
    }
}

public static partial class ProfileText
{
    public static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static void RequireName(Dictionary<string, string[]> errors, string key, string? value)
    {
        var cleaned = Clean(value);
        if (cleaned is null)
        {
            errors[key] = ["Campo obrigatório."];
        }
        else if (cleaned.Length > 255)
        {
            errors[key] = ["Use no máximo 255 caracteres."];
        }
    }

    [GeneratedRegex(@"^[0-9+()\- ]+$")]
    public static partial Regex PhonePattern();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

`dotnet run --project anamnys-aspire.Tests -- -method "*ProfileValidationTests*"`
Expected: all 12 test cases pass.

- [ ] **Step 5: Commit**

```bash
git add anamnys-aspire/anamnys-aspire.Server/Profile/ProfileContracts.cs anamnys-aspire/anamnys-aspire.Tests/Profile/ProfileValidationTests.cs
git commit -m "feat: add profile request/response contracts with validation"
```

---

### Task 5: Profile endpoints

**Files:**
- Create: `anamnys-aspire.Server/Profile/ProfileEndpoints.cs`
- Modify: `anamnys-aspire.Server/Program.cs` (after `phi.MapGet("probe", ...)`)
- Modify: `anamnys-aspire.Server/anamnys-aspire.Server.http`
- Test: `anamnys-aspire.Tests/Profile/ProfileEndpointTests.cs`

**Interfaces:**
- Consumes: Task 4 records and `Validate()`; `BrowserSession` (Task 1); `AuthSchemes.ProviderCookie`, `AuthSchemes.PatientCookie`; `CurrentUser.LocalIdOrNull()`.
- Produces: `static void MapProfileEndpoints(this RouteGroupBuilder phi)`; routes `GET|PUT /api/phi/providers/me/profile` and `GET|PUT /api/phi/patients/me/profile`. `GET` → `200` JSON (camelCase), `PUT` → `204`, invalid → `400` `ValidationProblem` (`errors` keyed as in Task 4), no session for that realm → `401`.

- [ ] **Step 1: Write the failing tests**

Create `anamnys-aspire.Tests/Profile/ProfileEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Profile;

// Only the seeded dev.provider exists in Keycloak (there is no seeded patient), so the
// patient routes are covered for their auth boundary here and for their rules in
// ProfileValidationTests.
[Collection(SharedAppHostCollection.Name)]
public class ProfileEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");

    [Theory]
    [InlineData("/api/phi/providers/me/profile")]
    [InlineData("/api/phi/patients/me/profile")]
    public async Task Profile_WithoutSession_Returns401(string path)
    {
        // Arrange
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", TestContext.Current.CancellationToken);

        // Act
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PatientProfile_WithOnlyAProviderSession_Returns401()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/phi/patients/me/profile"), TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProviderProfile_PutWithBlankName_Returns400WithNameError()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();

        // Act
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, "/api/phi/providers/me/profile")
            {
                Content = JsonContent.Create(new { name = " ", crpNumber = (string?)null, crpRegion = (string?)null }),
            },
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("errors").TryGetProperty("name", out _).Should().BeTrue();
    }

    [Fact]
    public async Task ProviderProfile_PutThenGet_ReturnsTheSavedValues()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var original = await GetProviderProfileAsync(session);
        var newName = $"Dev Provider {Guid.NewGuid():N}"[..24];

        try
        {
            // Act
            using var put = await session.SendAsync(
                new HttpRequestMessage(HttpMethod.Put, "/api/phi/providers/me/profile")
                {
                    Content = JsonContent.Create(new { name = newName, crpNumber = "06/99999", crpRegion = "SP" }),
                },
                TestContext.Current.CancellationToken);
            var saved = await GetProviderProfileAsync(session);

            // Assert
            put.StatusCode.Should().Be(HttpStatusCode.NoContent);
            saved.GetProperty("name").GetString().Should().Be(newName);
            saved.GetProperty("crpNumber").GetString().Should().Be("06/99999");
            saved.GetProperty("crpRegion").GetString().Should().Be("SP");
            saved.GetProperty("email").GetString().Should().Be(original.GetProperty("email").GetString());
        }
        finally
        {
            // Leave dev.provider as it was for anyone using the dev database.
            using var restore = await session.SendAsync(
                new HttpRequestMessage(HttpMethod.Put, "/api/phi/providers/me/profile")
                {
                    Content = JsonContent.Create(new
                    {
                        name = original.GetProperty("name").GetString(),
                        crpNumber = original.GetProperty("crpNumber").GetString(),
                        crpRegion = original.GetProperty("crpRegion").GetString(),
                    }),
                },
                TestContext.Current.CancellationToken);
        }
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026",
            TestContext.Current.CancellationToken);

    private static async Task<JsonElement> GetProviderProfileAsync(BrowserSession session)
    {
        using var response = await session.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/phi/providers/me/profile"), TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

`aspire stop`, then `dotnet run --project anamnys-aspire.Tests -- -method "*ProfileEndpointTests*"`
Expected: FAIL — routes do not exist (`404`).

- [ ] **Step 3: Implement the endpoints**

Create `anamnys-aspire.Server/Profile/ProfileEndpoints.cs`:

```csharp
using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Anamnys.Server.Profile;

public static class ProfileEndpoints
{
    // The realm is in the path rather than inferred from "whichever cookie
    // authenticated": one browser can hold a provider and a patient session at once
    // (distinct cookie names, all at Path=/, all sent to /api), and guessing could edit
    // the wrong person's profile. Each handler authenticates its own realm's cookie.
    public static void MapProfileEndpoints(this RouteGroupBuilder phi)
    {
        phi.MapGet("providers/me/profile", async (HttpContext httpContext, AnamnysDbContext db, CancellationToken cancellationToken) =>
        {
            if (await LocalIdForAsync(httpContext, AuthSchemes.ProviderCookie) is not { } localId)
            {
                return Results.Unauthorized();
            }

            var provider = await db.Providers.SingleOrDefaultAsync(p => p.Id == localId, cancellationToken);
            return provider is null
                ? Results.NotFound()
                : Results.Ok(new ProviderProfileResponse(provider.Email, provider.Name, provider.CrpNumber, provider.CrpRegion));
        });

        phi.MapPut("providers/me/profile", async (
            UpdateProviderProfileRequest request,
            HttpContext httpContext,
            AnamnysDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (await LocalIdForAsync(httpContext, AuthSchemes.ProviderCookie) is not { } localId)
            {
                return Results.Unauthorized();
            }

            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var provider = await db.Providers.SingleOrDefaultAsync(p => p.Id == localId, cancellationToken);
            if (provider is null)
            {
                return Results.NotFound();
            }

            provider.Name = ProfileText.Clean(request.Name)!;
            provider.CrpNumber = ProfileText.Clean(request.CrpNumber);
            provider.CrpRegion = ProfileText.Clean(request.CrpRegion);
            provider.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        phi.MapGet("patients/me/profile", async (HttpContext httpContext, AnamnysDbContext db, CancellationToken cancellationToken) =>
        {
            if (await LocalIdForAsync(httpContext, AuthSchemes.PatientCookie) is not { } localId)
            {
                return Results.Unauthorized();
            }

            var patient = await db.Patients.SingleOrDefaultAsync(p => p.Id == localId, cancellationToken);
            return patient is null
                ? Results.NotFound()
                : Results.Ok(new PatientProfileResponse(
                    patient.Email, patient.FirstName, patient.LastName, patient.Phone, patient.DateOfBirth));
        });

        phi.MapPut("patients/me/profile", async (
            UpdatePatientProfileRequest request,
            HttpContext httpContext,
            AnamnysDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (await LocalIdForAsync(httpContext, AuthSchemes.PatientCookie) is not { } localId)
            {
                return Results.Unauthorized();
            }

            var errors = request.Validate(DateOnly.FromDateTime(DateTime.UtcNow));
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var patient = await db.Patients.SingleOrDefaultAsync(p => p.Id == localId, cancellationToken);
            if (patient is null)
            {
                return Results.NotFound();
            }

            patient.FirstName = ProfileText.Clean(request.FirstName)!;
            patient.LastName = ProfileText.Clean(request.LastName)!;
            patient.Phone = ProfileText.Clean(request.Phone);
            patient.DateOfBirth = request.DateOfBirth;
            patient.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });
    }

    private static async Task<Guid?> LocalIdForAsync(HttpContext httpContext, string cookieScheme)
    {
        var result = await httpContext.AuthenticateAsync(cookieScheme);
        return result.Succeeded ? result.Principal!.LocalIdOrNull() : null;
    }
}
```

In `anamnys-aspire.Server/Program.cs`, add `using Anamnys.Server.Profile;` to the usings and, directly after the `phi.MapGet("probe", ...)` line:

```csharp
phi.MapProfileEndpoints();
```

- [ ] **Step 4: Document the routes**

Append to `anamnys-aspire.Server/anamnys-aspire.Server.http`:

```
### Own profile (needs the matching realm's session cookie — send from a signed-in browser)
GET {{Server_HostAddress}}/api/phi/providers/me/profile

###

PUT {{Server_HostAddress}}/api/phi/providers/me/profile
Content-Type: application/json

{ "name": "Ana Souza", "crpNumber": "06/12345", "crpRegion": "SP" }

###

GET {{Server_HostAddress}}/api/phi/patients/me/profile

###

PUT {{Server_HostAddress}}/api/phi/patients/me/profile
Content-Type: application/json

{ "firstName": "Ana", "lastName": "Souza", "phone": "+55 11 91234-5678", "dateOfBirth": "1990-01-02" }

###
```

- [ ] **Step 5: Run the tests to verify they pass**

`dotnet run --project anamnys-aspire.Tests -- -method "*ProfileEndpointTests*"`
Expected: `Total: 5, Errors: 0, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add anamnys-aspire/anamnys-aspire.Server anamnys-aspire/anamnys-aspire.Tests/Profile/ProfileEndpointTests.cs
git commit -m "feat: add realm-scoped own-profile endpoints for providers and patients"
```

---

### Task 6: Shared frontend — profile client, page components, account menu, copy

**Files:**
- Create: `packages/shared/src/api/profile.ts`
- Create: `packages/shared/src/components/AccountPage.tsx`
- Create: `packages/shared/src/components/ProfileEmailRow.tsx`
- Create: `packages/shared/src/components/SecurityPage.tsx`
- (the `AccountMenu` change is in Task 8, after both apps have the routes)
- Modify: `packages/shared/src/lib/i18n/locales/pt.json`

**Interfaces:**
- Consumes: Task 5 routes and JSON shapes; Task 2 `/auth/{realm}/action/{action}`.
- Produces:
  - `profileApi.getProvider(): Promise<ProviderProfile>`, `profileApi.updateProvider(body: ProviderProfileUpdate): Promise<void>`, `profileApi.getPatient(): Promise<PatientProfile>`, `profileApi.updatePatient(body: PatientProfileUpdate): Promise<void>`
  - `class ProfileValidationError extends Error { errors: Record<string, string[]> }`
  - `type ProviderProfile = { email: string; name: string; crpNumber: string | null; crpRegion: string | null }`
  - `type PatientProfile = { email: string | null; firstName: string; lastName: string; phone: string | null; dateOfBirth: string | null }`
  - `type AccountRealm = "provider" | "patient"`; `accountActionUrl(realm: AccountRealm, action: "UPDATE_PASSWORD" | "CONFIGURE_TOTP" | "UPDATE_EMAIL"): string`
  - `<AccountPage title subtitle>{children}</AccountPage>`
  - `<ProfileEmailRow realm email />`
  - `<SecurityPage realm />`


- [ ] **Step 1: Profile API client**

Create `packages/shared/src/api/profile.ts`:

```ts
// Native fetch, not the axios client in ./client.ts: that client's interceptor turns
// every error into a bare Error(message), and this form needs the 400 ValidationProblem
// body to show per-field messages. credentials: "include" is what sends the BFF's
// HttpOnly session cookie.
const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "/api";

export type AccountRealm = "provider" | "patient";

export type ProviderProfile = {
  email: string;
  name: string;
  crpNumber: string | null;
  crpRegion: string | null;
};
export type ProviderProfileUpdate = Omit<ProviderProfile, "email">;

export type PatientProfile = {
  email: string | null;
  firstName: string;
  lastName: string;
  phone: string | null;
  dateOfBirth: string | null;
};
export type PatientProfileUpdate = Omit<PatientProfile, "email">;

export class ProfileValidationError extends Error {
  readonly errors: Record<string, string[]>;

  constructor(errors: Record<string, string[]>) {
    super("Validation failed.");
    this.errors = errors;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE_URL}${path}`, {
    ...init,
    credentials: "include",
    headers: { "Content-Type": "application/json", ...init?.headers },
  });

  if (response.status === 400) {
    const body = (await response.json()) as { errors?: Record<string, string[]> };
    throw new ProfileValidationError(body.errors ?? {});
  }
  if (!response.ok) {
    throw new Error(`Request failed with status ${response.status}.`);
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}

export const profileApi = {
  getProvider: () => request<ProviderProfile>("/phi/providers/me/profile"),
  updateProvider: (body: ProviderProfileUpdate) =>
    request<void>("/phi/providers/me/profile", { method: "PUT", body: JSON.stringify(body) }),
  getPatient: () => request<PatientProfile>("/phi/patients/me/profile"),
  updatePatient: (body: PatientProfileUpdate) =>
    request<void>("/phi/patients/me/profile", { method: "PUT", body: JSON.stringify(body) }),
};

// A full-page navigation target, not a fetch: the BFF answers with a 302 to Keycloak,
// which runs the action and comes back to the current page.
export function accountActionUrl(
  realm: AccountRealm,
  action: "UPDATE_PASSWORD" | "CONFIGURE_TOTP" | "UPDATE_EMAIL"
): string {
  return `/auth/${realm}/action/${action}?returnUrl=${encodeURIComponent(window.location.pathname)}`;
}
```

- [ ] **Step 2: Copy**

In `packages/shared/src/lib/i18n/locales/pt.json`, replace the `accountMenu` block with:

```json
  "accountMenu": {
    "profile": "Dados pessoais",
    "security": "Acesso e segurança"
  },
  "accountPages": {
    "profile": {
      "subtitle": "Mantenha suas informações de cadastro atualizadas.",
      "email": "E-mail",
      "changeEmail": "Alterar e-mail",
      "changeEmailHint": "Enviaremos um link de confirmação para o novo endereço. A troca só vale depois que você clicar nele.",
      "name": "Nome completo",
      "crpNumber": "Número do CRP",
      "crpRegion": "Região do CRP",
      "firstName": "Nome",
      "lastName": "Sobrenome",
      "phone": "Telefone",
      "dateOfBirth": "Data de nascimento",
      "save": "Salvar alterações",
      "saved": "Dados atualizados.",
      "saveError": "Não foi possível salvar. Tente novamente."
    },
    "security": {
      "subtitle": "Gerencie sua senha e a autenticação em dois fatores.",
      "passwordTitle": "Senha",
      "passwordBody": "Altere a senha que você usa para entrar no Anamnys.",
      "passwordAction": "Alterar senha",
      "totpTitle": "Autenticação em dois fatores",
      "totpBody": "Proteja sua conta com um código extra gerado por um aplicativo autenticador.",
      "totpAction": "Configurar 2FA"
    }
  },
```

Validate: `node -e "JSON.parse(require('fs').readFileSync('packages/shared/src/lib/i18n/locales/pt.json','utf8'))"` prints nothing and exits 0.

- [ ] **Step 3: Page shell and email row**

Create `packages/shared/src/components/AccountPage.tsx`:

```tsx
import type { ReactNode } from "react";

// Shared frame for the account pages ("Dados pessoais", "Acesso e segurança").
export default function AccountPage({
  title,
  subtitle,
  children,
}: {
  title: string;
  subtitle: string;
  children: ReactNode;
}) {
  return (
    <div className="max-w-2xl mx-auto px-4 md:px-6 py-8 md:py-10">
      <h1 className="text-headline-md text-onSurface">{title}</h1>
      <p className="mt-1 mb-6 text-body-lg text-onSurfaceVariant">{subtitle}</p>
      <div className="bg-surfaceContainerLowest rounded-radii-lg border border-outlineVariant p-5 md:p-6">
        {children}
      </div>
    </div>
  );
}
```

Create `packages/shared/src/components/ProfileEmailRow.tsx`:

```tsx
import { useTranslation } from "react-i18next";
import { accountActionUrl, type AccountRealm } from "@anamnys/shared/api/profile";

// The email is the Keycloak login, so it is not an editable field here: changing it is
// Keycloak's UPDATE_EMAIL action, which confirms the new address before switching.
export default function ProfileEmailRow({ realm, email }: { realm: AccountRealm; email: string | null }) {
  const { t } = useTranslation();

  return (
    <div className="mb-5">
      <div className="text-label-sm text-onSurfaceVariant mb-1.5">{t("accountPages.profile.email")}</div>
      <div className="flex items-center justify-between gap-3">
        <span className="text-body-lg text-onSurface truncate">{email ?? "—"}</span>
        <a href={accountActionUrl(realm, "UPDATE_EMAIL")} className="text-label-lg text-primary shrink-0 hover:opacity-80">
          {t("accountPages.profile.changeEmail")}
        </a>
      </div>
      <p className="mt-1 text-body-md text-onSurfaceVariant">{t("accountPages.profile.changeEmailHint")}</p>
    </div>
  );
}
```

- [ ] **Step 4: Security page**

Create `packages/shared/src/components/SecurityPage.tsx`:

```tsx
import { KeyRound, Smartphone, type LucideIcon } from "lucide-react";
import { useTranslation } from "react-i18next";
import AccountPage from "@anamnys/shared/components/AccountPage";
import { accountActionUrl, type AccountRealm } from "@anamnys/shared/api/profile";

// Password and 2FA are Keycloak's; each card starts the matching Keycloak action,
// which renders with the Anamnys login theme and returns here.
export default function SecurityPage({ realm }: { realm: AccountRealm }) {
  const { t } = useTranslation();

  return (
    <AccountPage title={t("accountMenu.security")} subtitle={t("accountPages.security.subtitle")}>
      <SecurityRow
        Icon={KeyRound}
        title={t("accountPages.security.passwordTitle")}
        body={t("accountPages.security.passwordBody")}
        action={t("accountPages.security.passwordAction")}
        href={accountActionUrl(realm, "UPDATE_PASSWORD")}
      />
      <div className="h-px bg-outlineVariant my-5" />
      <SecurityRow
        Icon={Smartphone}
        title={t("accountPages.security.totpTitle")}
        body={t("accountPages.security.totpBody")}
        action={t("accountPages.security.totpAction")}
        href={accountActionUrl(realm, "CONFIGURE_TOTP")}
      />
    </AccountPage>
  );
}

function SecurityRow({
  Icon,
  title,
  body,
  action,
  href,
}: {
  Icon: LucideIcon;
  title: string;
  body: string;
  action: string;
  href: string;
}) {
  return (
    <div className="flex flex-col sm:flex-row sm:items-center gap-4">
      <Icon size={24} className="text-primary shrink-0" />
      <div className="flex-1">
        <div className="text-label-lg text-onSurface">{title}</div>
        <div className="text-body-md text-onSurfaceVariant mt-0.5">{body}</div>
      </div>
      <a
        href={href}
        className="shrink-0 text-center rounded-radii-md border border-primary text-primary px-4 py-2 text-label-lg hover:bg-primary/5"
      >
        {action}
      </a>
    </div>
  );
}
```

- [ ] **Step 5: Build and lint**

From `anamnys-aspire/`: `npm run build` then `npm run lint`.
Expected: both succeed.

- [ ] **Step 6: Commit**

```bash
git add anamnys-aspire/packages/shared
git commit -m "feat: add shared account pages and profile client"
```

---

### Task 7: Provider account pages

**Files:**
- Create: `apps/provider/src/routes/_app/account/profile.tsx`
- Create: `apps/provider/src/routes/_app/account/security.tsx`

**Interfaces:**
- Consumes: Task 6 `profileApi`, `ProfileValidationError`, `AccountPage`, `ProfileEmailRow`, `SecurityPage`; `useAuthStore().loadUser` (refreshes the name shown in the menu).

- [ ] **Step 1: Security route**

Create `apps/provider/src/routes/_app/account/security.tsx`:

```tsx
import { createFileRoute } from "@tanstack/react-router";
import SecurityPage from "@anamnys/shared/components/SecurityPage";

export const Route = createFileRoute("/_app/account/security")({
  component: () => <SecurityPage realm="provider" />,
});
```

- [ ] **Step 2: Profile route**

Create `apps/provider/src/routes/_app/account/profile.tsx`:

```tsx
import { useState, type FormEvent, type ReactNode } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import Button from "@anamnys/shared/ui/Button";
import TextField from "@anamnys/shared/ui/TextField";
import AccountPage from "@anamnys/shared/components/AccountPage";
import ProfileEmailRow from "@anamnys/shared/components/ProfileEmailRow";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import { profileApi, ProfileValidationError, type ProviderProfile } from "@anamnys/shared/api/profile";

export const Route = createFileRoute("/_app/account/profile")({ component: ProfilePage });

function ProfilePage() {
  const { t } = useTranslation();
  const { data } = useQuery({ queryKey: ["profile", "provider"], queryFn: profileApi.getProvider });

  return (
    <AccountPage title={t("accountMenu.profile")} subtitle={t("accountPages.profile.subtitle")}>
      {data && <ProfileForm key={data.email} initial={data} />}
    </AccountPage>
  );
}

function ProfileForm({ initial }: { initial: ProviderProfile }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const loadUser = useAuthStore((s) => s.loadUser);
  const [name, setName] = useState(initial.name);
  const [crpNumber, setCrpNumber] = useState(initial.crpNumber ?? "");
  const [crpRegion, setCrpRegion] = useState(initial.crpRegion ?? "");
  const [errors, setErrors] = useState<Record<string, string[]>>({});

  const save = useMutation({
    mutationFn: () => profileApi.updateProvider({ name, crpNumber: crpNumber || null, crpRegion: crpRegion || null }),
    onMutate: () => setErrors({}),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["profile", "provider"] });
      await loadUser();
    },
    onError: (error) => {
      if (error instanceof ProfileValidationError) setErrors(error.errors);
    },
  });

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    save.mutate();
  };

  return (
    <form onSubmit={onSubmit} noValidate>
      <ProfileEmailRow realm="provider" email={initial.email} />
      <Field error={errors.name?.[0]}>
        <TextField label={t("accountPages.profile.name")} value={name} onChange={(e) => setName(e.target.value)} />
      </Field>
      <div className="grid sm:grid-cols-2 gap-4">
        <Field error={errors.crpNumber?.[0]}>
          <TextField
            label={t("accountPages.profile.crpNumber")}
            value={crpNumber}
            onChange={(e) => setCrpNumber(e.target.value)}
          />
        </Field>
        <Field error={errors.crpRegion?.[0]}>
          <TextField
            label={t("accountPages.profile.crpRegion")}
            value={crpRegion}
            onChange={(e) => setCrpRegion(e.target.value)}
          />
        </Field>
      </div>
      <SaveFooter saving={save.isPending} saved={save.isSuccess} failed={save.isError && !(save.error instanceof ProfileValidationError)} />
    </form>
  );
}

function Field({ error, children }: { error?: string; children: ReactNode }) {
  return (
    <div className="mb-4">
      {children}
      {error && <p className="mt-1 text-body-md text-error">{error}</p>}
    </div>
  );
}

function SaveFooter({ saving, saved, failed }: { saving: boolean; saved: boolean; failed: boolean }) {
  const { t } = useTranslation();
  return (
    <div className="flex items-center gap-4 mt-2">
      <Button type="submit" title={t("accountPages.profile.save")} loading={saving} fullWidth={false} rounded="md" />
      {saved && <span className="text-body-md text-onSurfaceVariant">{t("accountPages.profile.saved")}</span>}
      {failed && <span className="text-body-md text-error">{t("accountPages.profile.saveError")}</span>}
    </div>
  );
}
```

- [ ] **Step 3: Regenerate the route tree, build and lint**

From `apps/provider/`: `npx vite build` (regenerates `src/routeTree.gen.ts`). Then from `anamnys-aspire/`: `npm run build` and `npm run lint`.
Expected: both succeed.

- [ ] **Step 4: Commit**

```bash
git add anamnys-aspire/apps/provider/src/routes/_app/account
git commit -m "feat: add the provider's Dados pessoais and Acesso e segurança pages"
```

---

### Task 8: Patient account pages

**Files:**
- Create: `apps/patient/src/routes/_app/account/profile.tsx`
- Create: `apps/patient/src/routes/_app/account/security.tsx`
- Modify: `packages/shared/src/ui/AccountMenu.tsx` — "Dados pessoais" / "Acesso e segurança" links (needs Task 7's and this task's routes to type-check)

**Interfaces:**
- Consumes: the same Task 6 exports as Task 7.

- [ ] **Step 1: Security route**

Create `apps/patient/src/routes/_app/account/security.tsx`:

```tsx
import { createFileRoute } from "@tanstack/react-router";
import SecurityPage from "@anamnys/shared/components/SecurityPage";

export const Route = createFileRoute("/_app/account/security")({
  component: () => <SecurityPage realm="patient" />,
});
```

- [ ] **Step 2: Profile route**

Create `apps/patient/src/routes/_app/account/profile.tsx`:

```tsx
import { useState, type FormEvent, type ReactNode } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import Button from "@anamnys/shared/ui/Button";
import TextField from "@anamnys/shared/ui/TextField";
import AccountPage from "@anamnys/shared/components/AccountPage";
import ProfileEmailRow from "@anamnys/shared/components/ProfileEmailRow";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import { profileApi, ProfileValidationError, type PatientProfile } from "@anamnys/shared/api/profile";

export const Route = createFileRoute("/_app/account/profile")({ component: ProfilePage });

function ProfilePage() {
  const { t } = useTranslation();
  const { data } = useQuery({ queryKey: ["profile", "patient"], queryFn: profileApi.getPatient });

  return (
    <AccountPage title={t("accountMenu.profile")} subtitle={t("accountPages.profile.subtitle")}>
      {data && <ProfileForm key={data.email ?? ""} initial={data} />}
    </AccountPage>
  );
}

function ProfileForm({ initial }: { initial: PatientProfile }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const loadUser = useAuthStore((s) => s.loadUser);
  const [firstName, setFirstName] = useState(initial.firstName);
  const [lastName, setLastName] = useState(initial.lastName);
  const [phone, setPhone] = useState(initial.phone ?? "");
  const [dateOfBirth, setDateOfBirth] = useState(initial.dateOfBirth ?? "");
  const [errors, setErrors] = useState<Record<string, string[]>>({});

  const save = useMutation({
    mutationFn: () =>
      profileApi.updatePatient({
        firstName,
        lastName,
        phone: phone || null,
        dateOfBirth: dateOfBirth || null,
      }),
    onMutate: () => setErrors({}),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["profile", "patient"] });
      await loadUser();
    },
    onError: (error) => {
      if (error instanceof ProfileValidationError) setErrors(error.errors);
    },
  });

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    save.mutate();
  };

  return (
    <form onSubmit={onSubmit} noValidate>
      <ProfileEmailRow realm="patient" email={initial.email} />
      <div className="grid sm:grid-cols-2 gap-4">
        <Field error={errors.firstName?.[0]}>
          <TextField
            label={t("accountPages.profile.firstName")}
            value={firstName}
            onChange={(e) => setFirstName(e.target.value)}
          />
        </Field>
        <Field error={errors.lastName?.[0]}>
          <TextField
            label={t("accountPages.profile.lastName")}
            value={lastName}
            onChange={(e) => setLastName(e.target.value)}
          />
        </Field>
      </div>
      <div className="grid sm:grid-cols-2 gap-4">
        <Field error={errors.phone?.[0]}>
          <TextField
            label={t("accountPages.profile.phone")}
            type="tel"
            value={phone}
            onChange={(e) => setPhone(e.target.value)}
          />
        </Field>
        <Field error={errors.dateOfBirth?.[0]}>
          <TextField
            label={t("accountPages.profile.dateOfBirth")}
            type="date"
            value={dateOfBirth}
            onChange={(e) => setDateOfBirth(e.target.value)}
          />
        </Field>
      </div>
      <SaveFooter saving={save.isPending} saved={save.isSuccess} failed={save.isError && !(save.error instanceof ProfileValidationError)} />
    </form>
  );
}

function Field({ error, children }: { error?: string; children: ReactNode }) {
  return (
    <div className="mb-4">
      {children}
      {error && <p className="mt-1 text-body-md text-error">{error}</p>}
    </div>
  );
}

function SaveFooter({ saving, saved, failed }: { saving: boolean; saved: boolean; failed: boolean }) {
  const { t } = useTranslation();
  return (
    <div className="flex items-center gap-4 mt-2">
      <Button type="submit" title={t("accountPages.profile.save")} loading={saving} fullWidth={false} rounded="md" />
      {saved && <span className="text-body-md text-onSurfaceVariant">{t("accountPages.profile.saved")}</span>}
      {failed && <span className="text-body-md text-error">{t("accountPages.profile.saveError")}</span>}
    </div>
  );
}
```

- [ ] **Step 3: Account menu links**

The menu links go last, once both apps have `/account/profile` and `/account/security`: TanStack Router type-checks a literal `to` against each app's route tree, so this does not compile earlier.

In `packages/shared/src/ui/AccountMenu.tsx`:
- imports become:

```tsx
import { useState } from "react";
import { Link } from "@tanstack/react-router";
import { LogOut, ShieldCheck, UserRound } from "lucide-react";
import { useTranslation } from "react-i18next";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import type { AccountRealm } from "@anamnys/shared/api/profile";
```

- the signature becomes `export default function AccountMenu({ realm }: { realm: AccountRealm }) {`
- the header comment becomes: `// Signed-in user's avatar: clicking it opens a menu with their name/email, links to their own profile and security pages, and Sign Out. \`realm\` picks which BFF session sign-out ends.`
- replace the `{/* A full-page navigation ... */}` comment and its `<a href={`/auth/${realm}/account`} ...>...</a>` with:

```tsx
            <Link to="/account/profile" onClick={() => setOpen(false)} className={itemClass}>
              <UserRound size={18} />
              {t("accountMenu.profile")}
            </Link>
            <Link to="/account/security" onClick={() => setOpen(false)} className={itemClass}>
              <ShieldCheck size={18} />
              {t("accountMenu.security")}
            </Link>
```

(`Link` resolves `/account/...` against each app's router `basepath`, so the same component works under `/provider/` and `/patient/`.)

- [ ] **Step 4: Regenerate the route tree, build and lint**

From `apps/patient/`: `npx vite build`. Then from `anamnys-aspire/`: `npm run build` and `npm run lint`.
Expected: both succeed.

- [ ] **Step 5: Commit**

```bash
git add anamnys-aspire/apps/patient/src/routes/_app/account anamnys-aspire/packages/shared/src/ui/AccountMenu.tsx
git commit -m "feat: add the patient account pages and link both apps' account menus to them"
```

---

### Task 9: Docs and end-to-end check

**Files:**
- Modify: `CLAUDE.md` (the "Keycloak owns TOTP enrolment…" gotcha)
- Modify: `documentation/13-provider-app-walkthrough.md` (the "## Settings" paragraph)

- [ ] **Step 1: Update the two references to the removed account-console link**

In `CLAUDE.md`, replace the gotcha bullet starting `- **Keycloak owns TOTP enrolment, password reset, and recovery codes.**` with:

```markdown
- **Keycloak owns TOTP enrolment, password changes, and the login email.** There is no
  password or 2FA form in any SPA. The account menu's "Acesso e segurança" page
  (`packages/shared/src/components/SecurityPage.tsx`) starts Keycloak application-initiated
  actions through the BFF's `/auth/{realm}/action/{action}` route, and "Alterar e-mail"
  does the same with `UPDATE_EMAIL`. That route allowlists the action; do not widen it
  without a reason.
```

In `documentation/13-provider-app-walkthrough.md`, replace the paragraph under `## Settings` (from `The account menu's *Acesso e segurança* item` through `"link to the Keycloak account console," not "build a form here."`) with:

```markdown
The account menu (`packages/shared/src/ui/AccountMenu.tsx`) leads to two pages under
`/account`. *Dados pessoais* edits the profile fields the application database owns
(`/api/phi/providers/me/profile`). *Acesso e segurança* does not implement password or
two-factor forms at all — a direct consequence of Chapter 6's identity design: each
button starts a Keycloak application-initiated action through the BFF's
`/auth/{realm}/action/{action}` route, rendered by the Anamnys login theme. If you're
ever asked to add a password or 2FA form in an SPA, the answer is "start the Keycloak
action," not "build a form here."
```

- [ ] **Step 2: Full test pass**

`aspire stop`, then `dotnet run --project anamnys-aspire.Tests`
Expected: all tests pass (`Failed: 0`).

- [ ] **Step 3: Manual browser pass**

`aspire start`, then in a browser:
1. Provider (`http://localhost:5273/provider/`), log in as `dev.provider@anamnys.local` / `DevProvider!2026`. Avatar menu shows "Dados pessoais" above "Acesso e segurança".
2. Dados pessoais: blank the name, save, and check the error under the field. Set a name, save, and check that "Dados atualizados." appears and the menu shows the new name.
3. Acesso e segurança → Alterar senha: Keycloak's themed page opens, then returns to `/provider/account/security` after the change or cancel.
4. Configurar 2FA: the themed OTP setup opens and returns the same way.
5. Dados pessoais → Alterar e-mail: Keycloak asks for the new email. A dev user's email (e.g. another seeded account) is refused as a duplicate. A fresh address gets a confirmation email (Mailpit or Resend). After clicking the link, log out and back in, and the profile shows the new email.
6. Repeat 1–4 on the patient app (`http://localhost:5274/patient/`) with a self-registered patient.

- [ ] **Step 4: Commit**

```bash
git add anamnys-aspire/CLAUDE.md anamnys-aspire/documentation/13-provider-app-walkthrough.md
git commit -m "docs: describe the account pages and Keycloak action route"
```
