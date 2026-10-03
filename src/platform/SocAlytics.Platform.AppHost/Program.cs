var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
var platformDatabase = postgres.AddDatabase("platform");

builder.AddProject<Projects.SocAlytics_Platform_Api>("api")
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health")
    .WithReference(platformDatabase)
    .WaitFor(platformDatabase);

builder.Build().Run();
