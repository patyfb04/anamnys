using System.Net.Sockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace Anamnys.Tests.Auth;

// One AppHost, shared across every test in the SchemeIsolation collection,
// instead of one per test: starting the whole distributed application four-
// plus times over just to hit two probe endpoints made the suite needlessly
// slow. Each test still gets its own HttpClient via CreateServerClient(), so
// a header set by one test (e.g. Authorization) can never leak into another.
public sealed class SchemeIsolationFixture : IAsyncLifetime
{
    private DistributedApplication? _app;

    public string KeycloakAdminUsername { get; private set; } = null!;
    public string KeycloakAdminPassword { get; private set; } = null!;

    // The fixed port 8080 AppHost.cs asks AddKeycloak for is not what ends up
    // reachable from this process: under Aspire.Hosting.Testing, DCP allocates
    // its own ephemeral host port for container endpoints rather than
    // honouring the AppHost's literal request, so "https://localhost:8080"
    // resolved to nothing here even once Keycloak itself was fully up.
    // CreateHttpClient resolves whatever port this test run actually got.
    public Uri KeycloakBaseAddress { get; private set; } = null!;

    public HttpClient CreateServerClient() => _app!.CreateHttpClient("server");

    public async ValueTask InitializeAsync()
    {
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.anamnys_aspire_AppHost>();

        var keycloakResource = appHost.Resources.OfType<KeycloakResource>().Single();
        KeycloakAdminUsername = await keycloakResource.AdminUserNameParameter!.GetValueAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("Keycloak admin username parameter had no value.");
        KeycloakAdminPassword = await keycloakResource.AdminPasswordParameter!.GetValueAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("Keycloak admin password parameter had no value.");

        _app = await appHost.BuildAsync();
        await _app.StartAsync();

        // The keycloak resource only registers "http" and "management"
        // endpoints under Aspire.Hosting.Testing — there is no separate
        // "https" endpoint to resolve (confirmed by enumerating
        // keycloakResource's EndpointAnnotations). The container's own log
        // confirms this too: "Listening on: http://0.0.0.0:8080 and
        // https://0.0.0.0:8443" — 8080 is plain HTTP. The apparent
        // https://localhost:8080 reachability under a full `aspire run` is
        // the CLI/dashboard's own tunnel proxy terminating TLS in front of
        // it; that proxy does not exist inside this xunit process, so the
        // "http" endpoint is what is actually reachable here.
        KeycloakBaseAddress = _app.CreateHttpClient("keycloak", "http").BaseAddress
            ?? throw new InvalidOperationException("Keycloak's http endpoint resolved to no base address.");

        // Each wait gets its own bounded timeout — sharing one
        // CancellationTokenSource meant the server wait could exhaust the
        // whole budget (image build/pull included) and leave the Keycloak
        // poll below with a single try before cancellation.
        using (var serverHealthTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
        {
            await _app.ResourceNotifications.WaitForResourceHealthyAsync("server", serverHealthTimeout.Token);
        }

        using (var keycloakReadyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
        {
            await WaitForKeycloakHttpReadyAsync(KeycloakBaseAddress, keycloakReadyTimeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private static async Task WaitForKeycloakHttpReadyAsync(Uri keycloakBaseAddress, CancellationToken cancellationToken)
    {
        using var client = InsecureKeycloakHttpClient.Create(keycloakBaseAddress);
        Exception? lastFailure = null;

        try
        {
            while (true)
            {
                try
                {
                    using var response = await client.GetAsync(
                        "realms/master/.well-known/openid-configuration", cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or SocketException)
                {
                    lastFailure = ex;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Keycloak's https endpoint at {keycloakBaseAddress} never became reachable within the timeout.",
                lastFailure);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SchemeIsolationCollection : ICollectionFixture<SchemeIsolationFixture>
{
    public const string Name = "SchemeIsolation";
}
