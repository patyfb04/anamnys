using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.Patients;

// HTTP boundary for invitations, end to end through Mailpit: the shared fixture forces the
// AppHost onto Mailpit (no Resend key), so the message can be read back from its API.
[Collection(SharedAppHostCollection.Name)]
public class PatientInvitationEndpointTests(SharedAppHostFixture fixture)
{
    private const string Patients = "/api/phi/providers/me/patients";
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("POST", "/api/phi/providers/me/patients/00000000-0000-0000-0000-000000000001/invitation")]
    [InlineData("DELETE", "/api/phi/providers/me/patients/00000000-0000-0000-0000-000000000001/portal-access")]
    [InlineData("POST", "/api/phi/patients/me/invitations/accept")]
    public async Task Routes_WithoutSession_Return401(string method, string path)
    {
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { token = "x" }) };

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Accept_WithAProviderSession_Returns401()
    {
        using var session = await LoginAsProviderAsync();

        using var response = await SendAsync(session, HttpMethod.Post, "/api/phi/patients/me/invitations/accept", new { token = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Invite_SendsTheInvitationThroughMailpit_AndTheRecordShowsIt()
    {
        // Arrange
        using var session = await LoginAsProviderAsync();
        var email = $"convite.{Guid.NewGuid():N}@exemplo.test";
        using var created = await SendAsync(session, HttpMethod.Post, Patients,
            new { firstName = "Convite", lastName = "Teste", contactEmail = email, dateOfBirth = "1990-01-01" });
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetString();
        var providerName = (await ReadJsonAsync(await SendAsync(session, HttpMethod.Get, "/api/phi/providers/me/profile")))
            .GetProperty("name").GetString();

        try
        {
            // Act
            using var invited = await SendAsync(session, HttpMethod.Post, $"{Patients}/{id}/invitation");
            var detail = await ReadJsonAsync(await SendAsync(session, HttpMethod.Get, $"{Patients}/{id}"));
            var text = await ReadMailpitTextAsync(email);

            // Assert
            invited.StatusCode.Should().Be(HttpStatusCode.NoContent);
            detail.GetProperty("portalStatus").GetString().Should().Be("invited");
            detail.GetProperty("invitationExpiresAt").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow.AddDays(6));
            text.Should().Contain($"{providerName} convidou você").And.Contain("http://localhost:5274/patient/convite/");
        }
        finally
        {
            using var _ = await SendAsync(session, HttpMethod.Delete, $"{Patients}/{id}");
        }
    }

    private async Task<string> ReadMailpitTextAsync(string to)
    {
        using var mailpit = fixture.CreateMailpitClient();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var search = await mailpit.GetFromJsonAsync<JsonElement>($"api/v1/search?query=to:%22{Uri.EscapeDataString(to)}%22", Ct);
            var messages = search.GetProperty("messages");
            if (messages.GetArrayLength() > 0)
            {
                var messageId = messages[0].GetProperty("ID").GetString();
                var message = await mailpit.GetFromJsonAsync<JsonElement>($"api/v1/message/{messageId}", Ct);
                return message.GetProperty("Text").GetString()!;
            }
            await Task.Delay(250, Ct);
        }
        throw new InvalidOperationException("The invitation never reached Mailpit.");
    }

    private static async Task<HttpResponseMessage> SendAsync(BrowserSession session, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await session.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private static Task<BrowserSession> LoginAsProviderAsync() =>
        BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);
}
