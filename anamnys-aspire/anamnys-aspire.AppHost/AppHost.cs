var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

var providerClientSecret = builder.AddParameter("provider-client-secret", secret: true);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();

var keycloakDb = postgres.AddDatabase("keycloakdb");
var anamnysDb = postgres.AddDatabase("anamnysdb");

// Fixed port, not a dynamic one: cookies and redirect URIs are bound to the
// origin, so a port that moves between AppHost restarts invalidates every
// session and every registered redirect URI.
var keycloak = builder.AddKeycloak("keycloak", 8080)
    .WithDockerfile("../keycloak")
    .WithPostgres(keycloakDb)
    .WithDataVolume()
    .WithOtlpExporter()
    .WithEnvironment("ANAMNYS_PROVIDER_CLIENT_SECRET", providerClientSecret)
    .WaitFor(keycloakDb);

var server = builder.AddProject<Projects.anamnys_aspire_Server>("server")
    .WithReference(cache)
    .WithReference(anamnysDb)
    .WithReference(keycloak)
    .WaitFor(cache)
    .WaitFor(anamnysDb)
    .WaitFor(keycloak)
    .WithEnvironment("ANAMNYS_PROVIDER_CLIENT_SECRET", providerClientSecret)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// Three SPAs, one server. Each is published into a sub-path of the server's
// wwwroot so all three stay same-origin with the API — which is what lets the
// BFF cookie auth work without CORS. The sub-paths must match the `base` option
// in each app's vite.config.ts.
var web = builder.AddViteApp("web", "../apps/web")
    .WithReference(server)
    .WaitFor(server);

var provider = builder.AddViteApp("provider", "../apps/provider")
    .WithReference(server)
    .WaitFor(server);

var patient = builder.AddViteApp("patient", "../apps/patient")
    .WithReference(server)
    .WaitFor(server);

server.PublishWithContainerFiles(web, "wwwroot");
server.PublishWithContainerFiles(provider, "wwwroot/provider");
server.PublishWithContainerFiles(patient, "wwwroot/patient");

builder.Build().Run();
