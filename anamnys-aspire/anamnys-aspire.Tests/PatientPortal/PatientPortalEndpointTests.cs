using System.Net;
using Anamnys.Tests.Auth;
using FluentAssertions;

namespace Anamnys.Tests.PatientPortal;

// The portal routes answer only to a patient-portal session. There is no seeded patient in
// Keycloak, so the happy path is covered in PatientPortalQueriesTests.
[Collection(SharedAppHostCollection.Name)]
public class PatientPortalEndpointTests
{
    private static readonly Uri ProviderBaseAddress = new("http://localhost:5273/");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/phi/patients/me/providers")]
    [InlineData("/api/phi/patients/me/providers/00000000-0000-0000-0000-000000000001/sessions?scope=upcoming")]
    public async Task Routes_WithoutSession_Return401(string path)
    {
        using var client = new HttpClient { BaseAddress = ProviderBaseAddress };
        await BrowserSession.WaitForDevServerAsync(client, "/provider/", Ct);

        using var response = await client.GetAsync(path, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/api/phi/patients/me/providers")]
    [InlineData("/api/phi/patients/me/providers/00000000-0000-0000-0000-000000000001/sessions?scope=upcoming")]
    public async Task Routes_WithAProviderSession_Return401(string path)
    {
        using var session = await BrowserSession.LoginAsync(
            ProviderBaseAddress, "/provider/", "provider", "dev.provider@anamnys.local", "DevProvider!2026", Ct);

        using var response = await session.SendAsync(new HttpRequestMessage(HttpMethod.Get, path), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
