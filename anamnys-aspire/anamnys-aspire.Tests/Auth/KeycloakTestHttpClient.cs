namespace Anamnys.Tests.Auth;

// Takes an explicit base address rather than hardcoding one: under
// Aspire.Hosting.Testing the fixed port 8080 AppHost.cs asks for is not what
// actually ends up reachable from the test process — DCP allocates its own
// ephemeral host port for container endpoints in test runs, resolved via
// DistributedApplication.CreateHttpClient and handed in here by
// SharedAppHostFixture. That resolved endpoint is plain HTTP (the "http"
// named endpoint — Keycloak's own https endpoint is not exposed as a
// separately named Aspire endpoint here), so no TLS bypass is needed.
internal static class KeycloakTestHttpClient
{
    public static HttpClient Create(Uri baseAddress) => new() { BaseAddress = baseAddress };
}
