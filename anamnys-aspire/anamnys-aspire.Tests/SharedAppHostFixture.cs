using System.Net.Sockets;
using Anamnys.Tests.Auth;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace Anamnys.Tests;

// One AppHost, shared across every test class in the SharedAppHost collection
// (Auth's scheme-isolation/guard tests and Data's break-glass constraint
// test), instead of one per test class. Two independent AppHosts each doing
// postgres.WithDataVolume() against the same named volume corrupted Postgres
// with "lock file postmaster.pid is empty" — that is the actual defect behind
// the original "serialize the whole suite" workaround. One shared AppHost per
// run removes the race outright, so xunit's default collection parallelism
// can stay on.
public sealed class SharedAppHostFixture : IAsyncLifetime
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

    public ValueTask<string?> GetConnectionStringAsync(string name, CancellationToken cancellationToken) =>
        _app!.GetConnectionStringAsync(name, cancellationToken);

    public async ValueTask InitializeAsync()
    {
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.anamnys_aspire_AppHost>(TestContext.Current.CancellationToken);

        var keycloakResource = appHost.Resources.OfType<KeycloakResource>().Single();
        KeycloakAdminUsername = await keycloakResource.AdminUserNameParameter!.GetValueAsync(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Keycloak admin username parameter had no value.");
        KeycloakAdminPassword = await keycloakResource.AdminPasswordParameter!.GetValueAsync(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Keycloak admin password parameter had no value.");

        _app = await appHost.BuildAsync(TestContext.Current.CancellationToken);
        await _app.StartAsync(TestContext.Current.CancellationToken);

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

        // Each wait gets its own bounded, linked timeout — sharing one
        // CancellationTokenSource meant the server wait could exhaust the
        // whole budget (image build/pull included) and leave the Keycloak
        // poll below with a single try before cancellation.
        using (var serverHealthTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            serverHealthTimeout.CancelAfter(TimeSpan.FromSeconds(120));
            await _app.ResourceNotifications.WaitForResourceHealthyAsync("server", serverHealthTimeout.Token);
        }

        using (var keycloakReadyTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            keycloakReadyTimeout.CancelAfter(TimeSpan.FromSeconds(90));
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
        using var client = KeycloakTestHttpClient.Create(keycloakBaseAddress);
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
                $"Keycloak's http endpoint at {keycloakBaseAddress} never became reachable within the timeout.",
                lastFailure);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SharedAppHostCollection : ICollectionFixture<SharedAppHostFixture>
{
    public const string Name = "SharedAppHost";
}
