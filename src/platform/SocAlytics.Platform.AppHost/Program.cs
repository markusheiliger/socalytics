var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
var database = postgres.AddDatabase("platform");

builder.AddProject<Projects.SocAlytics_Platform_Api>("api")
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health")
    .WithReference(database)
    .WaitFor(database);

builder.Build().Run();
