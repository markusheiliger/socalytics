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

var firstAdminPassword = builder.AddParameter("first-club-admin-password", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);
var firstAdminAccountName = builder.AddParameter("first-club-admin-account-name", "club-admin");
var clubDisplayName = builder.AddParameter("club-display-name", "Development Club");

var storageAccessKey = builder.AddParameter("socalytics-storage-access-key", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);
var storageSecretKey = builder.AddParameter("socalytics-storage-secret-key", new GenerateParameterDefault { MinLength = 24, Special = false }, secret: true, persist: true);

var rustfs = builder.AddContainer("rustfs", "rustfs/rustfs", "1.0.1")
    .WithHttpEndpoint(targetPort: 9000, name: "s3")
    .WithEnvironment("RUSTFS_ACCESS_KEY", storageAccessKey)
    .WithEnvironment("RUSTFS_SECRET_KEY", storageSecretKey)
    .WithHttpHealthCheck("/health", endpointName: "s3");
var rustfsEndpoint = rustfs.GetEndpoint("s3");

var api = builder.AddProject<Projects.SocAlytics_Platform_Api>("api")
    .WithHttpEndpoint()
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ClubBootstrap__FirstClubAdmin__InitialPassword", firstAdminPassword)
    .WithEnvironment("ClubBootstrap__FirstClubAdmin__AccountName", firstAdminAccountName)
    .WithEnvironment("ClubBootstrap__ClubDisplayName", clubDisplayName)
    .WithEnvironment("ObjectStorage__ServiceUrl", ReferenceExpression.Create($"{rustfsEndpoint.Property(EndpointProperty.Url)}"))
    .WithEnvironment("ObjectStorage__AccessKey", storageAccessKey)
    .WithEnvironment("ObjectStorage__SecretKey", storageSecretKey)
    .WithEnvironment("ObjectStorage__Bucket", "socalytics-recordings")
    .WithEnvironment("ObjectStorage__Region", "us-east-1")
    .WithEnvironment("ObjectStorage__EnsureBucketOnStartup", "true")
    .WithEnvironment("Recordings__Upload__SessionLifetime", "00:30:00")
    .WithEnvironment("Recordings__Upload__GrantLifetime", "00:15:00")
    .WithEnvironment("Recordings__Upload__ExpirySweepInterval", "00:01:00")
    .WithEnvironment("Recordings__Upload__MaxObjectSizeBytes", "5497558138880")
    .WithEnvironment("Recordings__Upload__MinPartSizeBytes", "5242880")
    .WithEnvironment("Recordings__Upload__MaxPartSizeBytes", "5368709120")
    .WithEnvironment("Recordings__Upload__MaxPartCount", "10000")
    .WithEnvironment("Recordings__Upload__MaxGrantsPerRequest", "1000")
    .WithEnvironment("Recordings__Upload__AllowedContentTypes__0", "video/mp4")
    .WithEnvironment("Recordings__Upload__AllowedContentTypes__1", "video/quicktime")
    .WithHttpHealthCheck("/health")
    .WithReference(appConnection)
    .WaitFor(postgres)
    .WaitFor(rustfs)
    .WaitForCompletion(migrator);

if (!string.Equals(builder.Configuration["SocAlytics:ApiHttpsEndpoint"], "false", StringComparison.OrdinalIgnoreCase))
{
    api.WithHttpsEndpoint();
}

builder.Build().Run();
