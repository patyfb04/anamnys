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
