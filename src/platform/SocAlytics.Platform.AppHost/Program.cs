var builder = DistributedApplication.CreateBuilder(args);

var database = builder.AddPostgres("postgres")
    .AddDatabase("platform");

builder.AddProject<Projects.SocAlytics_Platform_Api>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health");

builder.Build().Run();