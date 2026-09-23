var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.SocAlytics_Platform_Api>("api")
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health");

builder.Build().Run();