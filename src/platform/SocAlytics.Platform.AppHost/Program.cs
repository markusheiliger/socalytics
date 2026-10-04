var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
var platformDatabase = postgres.AddDatabase("platform");

builder.AddProject<Projects.SocAlytics_Platform_Api>("api")
    .WithReference(platformDatabase)
    .WaitFor(platformDatabase)
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/alive");

builder.Build().Run();
