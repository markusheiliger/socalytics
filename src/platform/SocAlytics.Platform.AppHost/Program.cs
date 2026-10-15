using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var e2eMode = string.Equals(Environment.GetEnvironmentVariable("SOCALYTICS_APPHOST_MODE"), "e2e", StringComparison.OrdinalIgnoreCase);
const int WebE2ePort = 4174;
const int MailpitE2ePort = 4176;

var generatedPassword = new GenerateParameterDefault { MinLength = 24, Special = false };
var migratorPassword = builder.AddParameter("socalytics-migrator-password", generatedPassword, secret: true, persist: true);
var appPassword = builder.AddParameter("socalytics-app-password", generatedPassword, secret: true, persist: true);

var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18")
    .WithEnvironment("POSTGRES_DB", "socalytics")
    .WithEnvironment("SOCALYTICS_MIGRATOR_PASSWORD", migratorPassword)
    .WithEnvironment("SOCALYTICS_APP_PASSWORD", appPassword)
    .WithInitFiles(Path.Combine(builder.AppHostDirectory, "PostgresInit"));

if (!e2eMode && !string.Equals(builder.Configuration["SocAlytics:LocalDatabase:Persistent"], "false", StringComparison.OrdinalIgnoreCase))
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

var natsTrust = new LocalNatsTrust();
var nats = builder.AddContainer("nats", "nats", "2.14")
    .WithEndpoint(targetPort: 4222, name: "client", scheme: "nats")
    .WithHttpEndpoint(targetPort: 8222, name: "monitor")
    .WithContainerFiles("/etc/nats", [new ContainerFile { Name = "nats.conf", Contents = natsTrust.BuildServerConfig() }])
    .WithArgs("-c", "/etc/nats/nats.conf")
    .WithHttpHealthCheck("/healthz", endpointName: "monitor");
var natsUrl = ReferenceExpression.Create($"{nats.GetEndpoint("client").Property(EndpointProperty.Url)}");

var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.31.5")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp")
    .WithHttpEndpoint(targetPort: 8025, name: "http");
var mailpitSmtp = mailpit.GetEndpoint("smtp");

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
    .WithEnvironment("Email__Smtp__Host", ReferenceExpression.Create($"{mailpitSmtp.Property(EndpointProperty.Host)}"))
    .WithEnvironment("Email__Smtp__Port", ReferenceExpression.Create($"{mailpitSmtp.Property(EndpointProperty.Port)}"))
    .WithEnvironment("Email__Smtp__Security", "StartTlsWhenAvailable")
    .WithEnvironment("Email__SenderAddress", "socalytics@localhost")
    .WithEnvironment("Email__SenderName", "SocAlytics")
    .WithEnvironment("Email__Confirmation__Lifetime", "1.00:00:00")
    .WithEnvironment("Email__Confirmation__MaxPerHour", "3")
    .WithEnvironment("Email__Confirmation__AttemptsPerMinute", "10")
    .WithEnvironment("Email__Delivery__MaxAttempts", "8")
    .WithEnvironment("Email__Delivery__PollInterval", "00:00:05")
    .WithEnvironment("Email__Delivery__LeaseDuration", "00:02:00")
    .WithEnvironment("Notifications__CoalescingWindow", "00:15:00")
    .WithEnvironment("Analysis__Results__GrantLifetime", "00:10:00")
    .WithEnvironment("Analysis__Results__MaxObjectsPerAttempt", "16")
    .WithEnvironment("Analysis__Results__MaxObjectBytes", "104857600")
    .WithEnvironment("Analysis__Results__MaxAttemptBytes", "1073741824")
    .WithEnvironment("Analysis__Results__MaxManifestBytes", "1048576")
    .WithEnvironment("Analysis__Reads__GrantLifetime", "00:10:00")
    .WithEnvironment("Analysis__Reads__MaxPageSize", "100")
    .WithHttpHealthCheck("/health")
    .WithReference(appConnection)
    .WaitFor(postgres)
    .WaitFor(rustfs)
    .WithEnvironment("ConnectionStrings__nats", natsUrl)
    .WaitFor(nats)
    .WithEnvironment("Nats__Authentication__Mode", "Jwt")
    .WithEnvironment("Nats__Authentication__OperatorSeed", natsTrust.OperatorSeed)
    .WithEnvironment("Nats__Authentication__AccountSeed", natsTrust.AccountSeed)
    .WithEnvironment("Nats__Authentication__SystemAccountSeed", natsTrust.SystemAccountSeed)
    .WithEnvironment("AnalystWork__QueueUrl", natsUrl)
    .WaitForCompletion(migrator);

api.WithEnvironment("Stamp__Id", "local-dev-stamp")
    .WithEnvironment("Registry__AllowSoftwareKeys", "true");

if (e2eMode)
{
    var e2eAdminPassword = Environment.GetEnvironmentVariable("SOCALYTICS_E2E_ADMIN_PASSWORD");
    if (string.IsNullOrWhiteSpace(e2eAdminPassword))
    {
        throw new InvalidOperationException("SOCALYTICS_E2E_ADMIN_PASSWORD must be set when SOCALYTICS_APPHOST_MODE is e2e.");
    }

    api.WithEnvironment("ClubBootstrap__FirstClubAdmin__InitialPassword", e2eAdminPassword);

    // The e2e API has no HTTPS endpoint; the scripted Analyst Manager declares the scheme through X-Forwarded-Proto.
    api.WithEnvironment("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true");
}

// The e2e mode keeps the HTTPS endpoint: the Manager endpoints require HTTPS, and the Web UI's proxy forwards to it.
if (builder.Configuration.GetValue("SocAlytics:ApiHttpsEndpoint", true))
{
    api.WithHttpsEndpoint();
    api.WithEnvironment("Stamp__PublicBaseUri", ReferenceExpression.Create($"{api.GetEndpoint("https").Property(EndpointProperty.Url)}"));
}
else
{
    api.WithEnvironment("Stamp__PublicBaseUri", ReferenceExpression.Create($"{api.GetEndpoint("http").Property(EndpointProperty.Url)}"));
}

// The Web UI proxies /api and /alive to the API, so the browser sees one origin.
const string webDirectory = "../../clients/web";
var web = builder.AddViteApp("web", webDirectory, e2eMode ? "preview" : "dev")
    .WithReference(api)
    .WaitFor(api)
    // Browser part uploads go straight to the object store, so the Web UI must allow that origin in its CSP
    // and the store must allow the Web UI origin in its CORS preflight (development only).
    .WithEnvironment("SOCALYTICS_OBJECT_STORAGE_ORIGIN", ReferenceExpression.Create($"{rustfsEndpoint.Property(EndpointProperty.Url)}"));
rustfs.WithEnvironment("RUSTFS_CORS_ALLOWED_ORIGINS", ReferenceExpression.Create($"{web.GetEndpoint("http").Property(EndpointProperty.Url)}"));

api.WithEnvironment("Notifications__WebUiBaseUri", ReferenceExpression.Create($"{web.GetEndpoint("http").Property(EndpointProperty.Url)}"));

if (e2eMode)
{
    // The built Web UI is served by `vite preview` on a fixed origin for the browser tests.
    web.WithEndpoint("http", endpoint => endpoint.Port = WebE2ePort);

    // The browser tests read the confirmation messages from Mailpit's REST API on a fixed port.
    mailpit.WithEndpoint("http", endpoint => endpoint.Port = MailpitE2ePort);

    // The endpoint reference above resolves to the container network's host alias, which is not the origin the browser
    // sends; the fixed e2e port makes the browser origins known.
    rustfs.WithEnvironment("RUSTFS_CORS_ALLOWED_ORIGINS", string.Concat("http://127.0.0.1:", WebE2ePort.ToString(), ",http://localhost:", WebE2ePort.ToString()));
}

// Managers print this origin as the address where a person enters the pairing code.
api.WithEnvironment("Registry__Pairing__VerificationBaseUri", ReferenceExpression.Create($"{web.GetEndpoint("http").Property(EndpointProperty.Url)}"));

builder.Build().Run();
