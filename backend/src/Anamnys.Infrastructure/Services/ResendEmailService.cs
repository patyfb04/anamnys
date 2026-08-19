using System.Net.Http.Headers;
using System.Net.Http.Json;
using Anamnys.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace Anamnys.Infrastructure.Services;

public class ResendOptions
{
    /// <summary>Secret API key — set via `dotnet user-secrets set "Resend:ApiKey" "re_..."` in
    /// dev, never committed to appsettings.*.json.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Must be an address on a domain verified in the Resend account, or the sandbox
    /// address `onboarding@resend.dev` (which only delivers to the account owner's own email).</summary>
    public string FromAddress { get; set; } = "onboarding@resend.dev";
}

/// <summary>
/// Thin wrapper over Resend's REST API (https://resend.com/docs/api-reference/emails/send-email)
/// via a typed HttpClient — no SDK dependency, the API surface used here is a single POST.
/// </summary>
public class ResendEmailService : IEmailService
{
    private readonly HttpClient _http;
    private readonly ResendOptions _opts;

    public ResendEmailService(HttpClient http, IOptions<ResendOptions> opts)
    {
        _opts = opts.Value;
        _http = http;
        _http.BaseAddress = new Uri("https://api.resend.com/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);
    }

    public async Task SendAsync(string toAddress, string subject, string htmlBody, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("emails", new
        {
            from = _opts.FromAddress,
            to = new[] { toAddress },
            subject,
            html = htmlBody,
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Resend API returned {(int)response.StatusCode}: {body}");
        }
    }
}
