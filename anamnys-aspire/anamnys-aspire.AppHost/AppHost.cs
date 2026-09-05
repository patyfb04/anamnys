var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

var providerClientSecret = builder.AddParameter("provider-client-secret", secret: true);
var patientClientSecret = builder.AddParameter("patient-client-secret", secret: true);
var ownerClientSecret = builder.AddParameter("owner-client-secret", secret: true);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();

var keycloakDb = postgres.AddDatabase("keycloakdb");
var anamnysDb = postgres.AddDatabase("anamnysdb");

// Declared before `keycloak` so ANAMNYS_APP_ORIGIN can reference this project's
// endpoint without creating a WaitFor cycle: keycloak depends on server's endpoint,
// so server cannot also WaitFor(keycloak) until keycloak exists (see below).
var server = builder.AddProject<Projects.anamnys_aspire_Server>("server")
    .WithReference(cache)
    .WithReference(anamnysDb)
    .WaitFor(cache)
    .WaitFor(anamnysDb)
    .WithEnvironment("ANAMNYS_PROVIDER_CLIENT_SECRET", providerClientSecret)
    .WithEnvironment("ANAMNYS_PATIENT_CLIENT_SECRET", patientClientSecret)
    .WithEnvironment("ANAMNYS_OWNER_CLIENT_SECRET", ownerClientSecret)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// Fixed port, not a dynamic one: cookies and redirect URIs are bound to the
// origin, so a port that moves between AppHost restarts invalidates every
// session and every registered redirect URI.
var keycloak = builder.AddKeycloak("keycloak", 8080)
    .WithDockerfile("../keycloak")
    .WithPostgres(keycloakDb)
    .WithDataVolume()
    .WithOtlpExporter()
    .WithEnvironment("ANAMNYS_PROVIDER_CLIENT_SECRET", providerClientSecret)
    .WithEnvironment("ANAMNYS_PATIENT_CLIENT_SECRET", patientClientSecret)
    .WithEnvironment("ANAMNYS_OWNER_CLIENT_SECRET", ownerClientSecret)
    .WithEnvironment("ANAMNYS_APP_ORIGIN", server.GetEndpoint("http"))
    .WaitFor(keycloakDb);

server.WithReference(keycloak).WaitFor(keycloak);

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
