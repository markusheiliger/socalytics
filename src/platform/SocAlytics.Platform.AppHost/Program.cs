var builder = DistributedApplication.CreateBuilder(args);

var generatedPassword = new GenerateParameterDefault { MinLength = 24, Special = false };
var migratorPassword = builder.AddParameter("socalytics-migrator-password", generatedPassword, secret: true, persist: true);
var appPassword = builder.AddParameter("socalytics-app-password", generatedPassword, secret: true, persist: true);

var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18")
    .WithEnvironment("POSTGRES_DB", "socalytics")
    .WithEnvironment("SOCALYTICS_MIGRATOR_PASSWORD", migratorPassword)
    .WithEnvironment("SOCALYTICS_APP_PASSWORD", appPassword)
    .WithInitFiles(Path.Combine(builder.AppHostDirectory, "PostgresInit"));

if (!string.Equals(builder.Configuration["SocAlytics:LocalDatabase:Persistent"], "false", StringComparison.OrdinalIgnoreCase))
{
    postgres.WithDataVolume("socalytics-postgres-data");
}

var endpoint = postgres.Resource.PrimaryEndpoint;
var migratorConnection = builder.AddConnectionString(
    "socalytics-migrator",
    ReferenceExpression.Create($"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Username=socalytics_migrator;Password={migratorPassword};Database=socalytics"));
var appConnection = builder.AddConnectionString(
    "socalytics",
    ReferenceExpression.Create($"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Username=socalytics_app;Password={appPassword};Database=socalytics"));

var migrator = builder.AddProject<Projects.SocAlytics_Platform_Migrator>("migrator")
    .WithReference(migratorConnection)
    .WaitFor(postgres);

builder.AddProject<Projects.SocAlytics_Platform_Api>("api")
    .WithHttpEndpoint()
    .WithHttpHealthCheck("/health")
    .WithReference(appConnection)
    .WaitFor(postgres)
    .WaitForCompletion(migrator);

builder.Build().Run();
