var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

var server = builder.AddProject<Projects.anamnys_aspire_Server>("server")
    .WithReference(cache)
    .WaitFor(cache)
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
