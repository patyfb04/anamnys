using System.Net;
using System.Text.Json;
using Anamnys.Server.Email;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace Anamnys.Tests.Email;

public class EmailSenderTests
{
    private static readonly EmailMessage Message = new("ana@example.com", "Ana", "Assunto", "Texto", "<p>Html</p>");

    private static IOptions<EmailOptions> Options(string provider) => Microsoft.Extensions.Options.Options.Create(new EmailOptions
    {
        Provider = provider,
        ResendApiKey = "re_test_key",
        From = "noreply@anamnys.test",
        FromName = "Anamnys",
        MailpitBaseUrl = "http://localhost:8025",
    });

    [Fact]
    public async Task Resend_PostsTheMessageToItsApiWithTheBearerKey()
    {
        // Arrange
        var http = new CapturingHandler(HttpStatusCode.OK);
        var sender = new ResendEmailSender(new HttpClient(http), Options("resend"));

        // Act
        await sender.SendAsync(Message, TestContext.Current.CancellationToken);

        // Assert
        http.Request!.Method.Should().Be(HttpMethod.Post);
        http.Request.RequestUri.Should().Be(new Uri("https://api.resend.com/emails"));
        http.Request.Headers.Authorization!.ToString().Should().Be("Bearer re_test_key");
        var body = JsonDocument.Parse(http.Body!).RootElement;
        body.GetProperty("from").GetString().Should().Be("Anamnys <noreply@anamnys.test>");
        body.GetProperty("to")[0].GetString().Should().Be("ana@example.com");
        body.GetProperty("subject").GetString().Should().Be("Assunto");
        body.GetProperty("text").GetString().Should().Be("Texto");
        body.GetProperty("html").GetString().Should().Be("<p>Html</p>");
    }

    [Fact]
    public async Task Mailpit_PostsTheMessageToItsSendApi()
    {
        // Arrange
        var http = new CapturingHandler(HttpStatusCode.OK);
        var sender = new MailpitEmailSender(new HttpClient(http), Options("mailpit"));

        // Act
        await sender.SendAsync(Message, TestContext.Current.CancellationToken);

        // Assert
        http.Request!.RequestUri.Should().Be(new Uri("http://localhost:8025/api/v1/send"));
        var body = JsonDocument.Parse(http.Body!).RootElement;
        body.GetProperty("From").GetProperty("Email").GetString().Should().Be("noreply@anamnys.test");
        body.GetProperty("To")[0].GetProperty("Email").GetString().Should().Be("ana@example.com");
        body.GetProperty("To")[0].GetProperty("Name").GetString().Should().Be("Ana");
        body.GetProperty("Subject").GetString().Should().Be("Assunto");
        body.GetProperty("HTML").GetString().Should().Be("<p>Html</p>");
    }

    [Theory]
    [InlineData("resend")]
    [InlineData("mailpit")]
    public async Task ARejectedSend_ThrowsEmailSendException(string provider)
    {
        // Arrange
        var http = new CapturingHandler(HttpStatusCode.UnprocessableEntity);
        IEmailSender sender = provider == "resend"
            ? new ResendEmailSender(new HttpClient(http), Options(provider))
            : new MailpitEmailSender(new HttpClient(http), Options(provider));

        // Act
        var act = () => sender.SendAsync(Message, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<EmailSendException>();
    }

    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent("{}") };
        }
    }
}
