namespace Anamnys.Tests.Auth;

// Keycloak is https-only with a self-signed certificate — the same reason
// local curls against it need -k. It takes an explicit base address rather
// than hardcoding one: under Aspire.Hosting.Testing the fixed port 8080
// AppHost.cs asks for is not what actually ends up reachable from the test
// process — DCP allocates its own ephemeral host port for container
// endpoints in test runs, resolved via DistributedApplication.CreateHttpClient
// and handed in here by SchemeIsolationFixture.
internal static class InsecureKeycloakHttpClient
{
    public static HttpClient Create(Uri baseAddress) =>
        new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        {
            BaseAddress = baseAddress,
        };
}
