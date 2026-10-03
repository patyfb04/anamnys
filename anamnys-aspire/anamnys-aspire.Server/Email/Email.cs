using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.Email;

// Outgoing email. Two HTTP senders, chosen by configuration: Resend's API, and in dev runs
// without a Resend key, Mailpit's send API so mail lands in the local catcher
// (design/specs/2026-10-03-portal-invitation-design.md §5). Never log message content or
// recipients: an invitation reveals a care relationship.
public sealed record EmailMessage(string ToEmail, string ToName, string Subject, string Text, string Html);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

public sealed class EmailSendException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class EmailOptions
{
    public const string Section = "Email";

    public string? Provider { get; set; }
    public string? ResendApiKey { get; set; }
    public string From { get; set; } = "";
    public string FromName { get; set; } = "Anamnys";
    public string? MailpitBaseUrl { get; set; }
}

public sealed class ResendEmailSender(HttpClient http, IOptions<EmailOptions> options) : IEmailSender
{
    private static readonly Uri Endpoint = new("https://api.resend.com/emails");

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                from = $"{settings.FromName} <{settings.From}>",
                to = new[] { message.ToEmail },
                subject = message.Subject,
                text = message.Text,
                html = message.Html,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResendApiKey);
        await EmailHttp.SendAsync(http, request, "Resend", cancellationToken);
    }
}

public sealed class MailpitEmailSender(HttpClient http, IOptions<EmailOptions> options) : IEmailSender
{
    // Mailpit documents PascalCase fields (From, To, HTML); send them exactly so.
    private static readonly System.Text.Json.JsonSerializerOptions ExactNames = new() { PropertyNamingPolicy = null };

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.MailpitBaseUrl!), "api/v1/send"))
        {
            Content = JsonContent.Create(new
            {
                From = new { Email = settings.From, Name = settings.FromName },
                To = new[] { new { Email = message.ToEmail, Name = message.ToName } },
                Subject = message.Subject,
                Text = message.Text,
                HTML = message.Html,
            }, options: ExactNames),
        };
        await EmailHttp.SendAsync(http, request, "Mailpit", cancellationToken);
    }
}

internal static class EmailHttp
{
    // The provider's error body is not included: it can echo the recipient address.
    public static async Task SendAsync(HttpClient http, HttpRequestMessage request, string provider, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new EmailSendException($"{provider} could not be reached.", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new EmailSendException($"{provider} rejected the message with status {(int)response.StatusCode}.");
            }
        }
    }
}

public static class EmailServiceCollectionExtensions
{
    // Registers IEmailSender only when Email:Provider is set; features that send mail must
    // treat a missing sender as "email not configured" rather than fail at startup.
    public static IHostApplicationBuilder AddEmail(this IHostApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(EmailOptions.Section);
        builder.Services.Configure<EmailOptions>(section);

        switch (section["Provider"])
        {
            case "resend":
                builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>();
                break;
            case "mailpit":
                builder.Services.AddHttpClient<IEmailSender, MailpitEmailSender>();
                break;
        }

        return builder;
    }
}
