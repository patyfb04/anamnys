using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Anamnys.Tests.Auth;

// Task 6's review asked for an assertion that the session cookie carries no
// token material: SessionStore/RedisTicketStore exist precisely to keep the
// access and refresh tokens server-side, and if that were ever silently
// bypassed there is no other error anywhere that would catch it — the cookie
// would just start carrying the raw JWTs with nobody the wiser. This drives
// a genuine owners-realm login (the seeded `dev.owner` account, see
// keycloak/realms/anamnys-owners.json) through the real challenge/callback
// flow and inspects the actual `__Host-anamnys-owner` cookie value, rather
// than trusting that SaveTokens + ITicketStore keeps working.
[Collection(SharedAppHostCollection.Name)]
public class OwnerSessionCookieTests
{
    // Not fixture.CreateServerClient(): the redirect_uri the server builds for
    // the OIDC challenge is derived from the Host header of the inbound
    // request (see AppHost.cs's port-pinning comment and each app's
    // vite.config.ts /auth proxy, which forwards Host unchanged), and only
    // "http://localhost:5276/signin-oidc-owner" is a registered redirect URI
    // for anamnys-admin-web (keycloak/realms/anamnys-owners.json). Hitting the
    // server's own ephemeral test port directly sends a Host header Keycloak
    // rejects with "Invalid parameter: redirect_uri" — this test has to go
    // through the same fixed admin dev-server port a real browser would.
    private static readonly Uri AdminBaseAddress = new("http://localhost:5276/");

    [Fact]
    public async Task OwnerLogin_SessionCookie_CarriesNoTokenMaterialAndStaysSmall()
    {
        // Arrange — a hand-rolled cookie jar and manual redirect-following,
        // not CookieContainer + HttpClientHandler.AllowAutoRedirect. Two
        // reasons neither works here:
        //  1. CookieContainer enforces the Secure-attribute-requires-https
        //     rule literally against the request's URI scheme. Real browsers
        //     (and curl) carve out an exception for http://localhost as a
        //     "potentially trustworthy origin" so Secure cookies still work
        //     there in dev; CookieContainer has no such carve-out, so it
        //     silently omits every Secure cookie (the OIDC correlation cookie
        //     included) from requests to this plain-http dev origin, and the
        //     callback fails with "Correlation failed" — confirmed by
        //     reproducing outside this test with GetCookieHeader returning "".
        //  2. AllowAutoRedirect=true only ever exposes the *final* response
        //     of a redirect chain to calling code — every Set-Cookie header
        //     from an intermediate hop (in particular the challenge's own
        //     302, which is where the correlation cookie is actually set) is
        //     invisible to anything watching the outer HttpResponseMessage.
        //     Manually walking each redirect is the only way to see them all.
        var jar = new Dictionary<string, string>();
        using var innerHandler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(innerHandler) { BaseAddress = AdminBaseAddress };

        // The admin Vite dev server is a separate resource from "server" (see
        // AppHost.cs); SharedAppHostFixture only waits for "server" and
        // "keycloak" to be ready, so poll briefly for the admin dev server
        // itself to come up before driving the actual login through it.
        await WaitForAdminDevServerAsync(client, TestContext.Current.CancellationToken);

        // Act — GET the challenge endpoint; follow every redirect by hand so
        // the correlation cookie set on the very first 302 is captured.
        var loginPage = await SendFollowingRedirectsAsync(
            client, jar, new HttpRequestMessage(HttpMethod.Get, new Uri(AdminBaseAddress, "/auth/owner/login?returnUrl=/admin/")),
            TestContext.Current.CancellationToken);
        var html = await loginPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        loginPage.IsSuccessStatusCode.Should().BeTrue(
            $"the challenge/redirect/login-page chain must succeed; got {(int)loginPage.StatusCode} " +
            $"from {loginPage.RequestMessage?.RequestUri}. Body: {html}");

        // The login page is a keycloakify theme: the form itself
        // (apps/keycloak-theme/src/login/pages/Login.tsx, action={url.loginAction})
        // is rendered client-side by React, hydrated from a `kcContext` object
        // Keycloak embeds as a raw JS object literal in an inline <script> —
        // there is no server-rendered <form> tag for a plain HttpClient to
        // scrape. Pull url.loginAction straight out of that embedded context.
        var actionMatch = Regex.Match(html, "\"loginAction\"\\s*:\\s*\"([^\"]+)\"");
        actionMatch.Success.Should().BeTrue("the Keycloak-hosted login page must embed kcContext.url.loginAction");
        var formAction = actionMatch.Groups[1].Value.Replace("\\/", "/");

        // Submit dev.owner's credentials (seeded in keycloak/realms/anamnys-owners.json,
        // stripped from non-Development images by strip-dev-seed.jq) to that action —
        // this drives Keycloak's real authentication, not a stub.
        var submission = await SendFollowingRedirectsAsync(
            client, jar,
            new HttpRequestMessage(HttpMethod.Post, formAction)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = "dev.owner@anamnys.local",
                    ["password"] = "SupportStaff!2026Dev",
                }),
            },
            TestContext.Current.CancellationToken);
        var submissionBody = await submission.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        submission.IsSuccessStatusCode.Should().BeTrue(
            $"credential submission and the callback it triggers must succeed; got {(int)submission.StatusCode} " +
            $"from {submission.RequestMessage?.RequestUri}. Body: {submissionBody}");

        // Keycloak's realm uses the default response_mode=form_post: the
        // credential POST above lands on an intermediate Keycloak page whose
        // <BODY Onload="document.forms[0].submit()"> auto-submits a hidden
        // form back to the real callback — a real browser does this via JS,
        // which HttpClient never executes, so it has to be replayed by hand.
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

        var callback = await SendFollowingRedirectsAsync(
            client, jar,
            new HttpRequestMessage(HttpMethod.Post, callbackAction) { Content = new FormUrlEncodedContent(hiddenFields) },
            TestContext.Current.CancellationToken);
        var callbackBody = await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        callback.IsSuccessStatusCode.Should().BeTrue(
            $"replaying the form_post callback must succeed; got {(int)callback.StatusCode} " +
            $"from {callback.RequestMessage?.RequestUri}. Body: {callbackBody}");
        callback.RequestMessage?.RequestUri?.PathAndQuery.Should().NotContain(
            "authError", $"the callback must not fail server-side. Final URI: {callback.RequestMessage?.RequestUri}");

        // Assert — the session cookie exists and holds an opaque reference,
        // never the tokens themselves.
        jar.TryGetValue("__Host-anamnys-owner", out var sessionCookieValue).Should().BeTrue(
            $"a successful owner login must set the session cookie. Jar contents: {string.Join(", ", jar.Keys)}");
        sessionCookieValue.Should().NotContain(
            "eyJ", "the cookie must hold an opaque ticket reference, never a JWT — SessionStore/ITicketStore " +
            "keep the actual access and refresh tokens server-side");
        sessionCookieValue!.Length.Should().BeLessThan(
            500, "a few hundred bytes at most: the cookie is a ticket-store key, not the serialized ticket itself");
    }

    private static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpClient client, Dictionary<string, string> jar, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        while (true)
        {
            ApplyCookies(jar, request);
            var response = await client.SendAsync(request, cancellationToken);
            CaptureCookies(jar, response);

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

    private static void ApplyCookies(Dictionary<string, string> jar, HttpRequestMessage request)
    {
        if (jar.Count == 0)
        {
            return;
        }

        request.Headers.Remove("Cookie");
        request.Headers.Add("Cookie", string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    private static void CaptureCookies(Dictionary<string, string> jar, HttpResponseMessage response)
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
                jar.Remove(name);
            }
            else
            {
                jar[name] = value;
            }
        }
    }

    private static async Task WaitForAdminDevServerAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/admin/", timeout.Token);
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
}
