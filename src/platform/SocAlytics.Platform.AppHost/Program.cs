using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var e2eMode = string.Equals(Environment.GetEnvironmentVariable("SOCALYTICS_APPHOST_MODE"), "e2e", StringComparison.OrdinalIgnoreCase);
const int WebE2ePort = 4174;
const int MailpitE2ePort = 4175;
const int OidcProviderE2ePort = 4190;

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
    .WithEnvironment("Analysis__Results__MaxFactArtifactBytes", "16777216")
    .WithEnvironment("Analysis__Results__MaxFactPayloadBytes", "16384")
    .WithEnvironment("Analysis__Results__MaxFactsPerResult", "50000")
    .WithEnvironment("Analysis__Facts__DefaultPageSize", "100")
    .WithEnvironment("Analysis__Facts__MaxPageSize", "500")
    .WithEnvironment("Analysis__Reads__GrantLifetime", "00:10:00")
    .WithEnvironment("Analysis__Reads__MaxPageSize", "100")
    .WithEnvironment("Analysis__Evidence__GrantLifetime", "00:05:00")
    .WithEnvironment("Analysis__Evidence__RetryAfter", "00:00:05")
    .WithEnvironment("Analysis__Evidence__ResolutionsPerMinute", "60")
    .WithEnvironment("Analysis__Evidence__MaxDuration", "00:10:00")
    .WithEnvironment("Analysis__Evidence__MaxContext", "00:00:10")
    .WithEnvironment("Segments__Toolchain__FfmpegPath", Environment.GetEnvironmentVariable("SOCALYTICS_FFMPEG_PATH") ?? "ffmpeg")
    .WithEnvironment("Segments__Toolchain__FfprobePath", Environment.GetEnvironmentVariable("SOCALYTICS_FFPROBE_PATH") ?? "ffprobe")
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
    .WithEnvironment("Registry__SoftwareKeys", "all");

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

    // The test OpenID Connect provider issues an ID token for a fixed subject without an interactive login. It listens on a
    // fixed port so the issuer is the same for the browser and the API.
    var testProvider = builder.AddContainer("oidc-provider", "ghcr.io/navikt/mock-oauth2-server")
        .WithImageSHA256("65d4ed47ce094cc3849e41e03499bfb32372d69c726ec22b3d5a19eba9bc787e")
        .WithHttpEndpoint(port: OidcProviderE2ePort, targetPort: 8080, name: "http")
        .WithEnvironment("JSON_CONFIG", """{"interactiveLogin":false,"httpServer":"NettyWrapper","tokenCallbacks":[{"issuerId":"test","requestMappings":[{"requestParam":"grant_type","match":"authorization_code","claims":{"sub":"mock-subject-1"}}]}]}""")
        .WithHttpHealthCheck("/test/.well-known/openid-configuration");

    api.WithEnvironment("IdentityAccess__ExternalProviders__test__DisplayName", "Test provider")
        .WithEnvironment("IdentityAccess__ExternalProviders__test__Authority", string.Concat("http://127.0.0.1:", OidcProviderE2ePort.ToString(), "/test"))
        .WithEnvironment("IdentityAccess__ExternalProviders__test__ClientId", "socalytics")
        .WithEnvironment("IdentityAccess__ExternalProviders__test__ClientSecret", "e2e-provider-client-secret")
        .WithEnvironment("IdentityAccess__ExternalSignIn__WebUiBaseUrl", string.Concat("http://127.0.0.1:", WebE2ePort.ToString()))
        .WaitFor(testProvider);
}

// The e2e mode keeps the HTTPS endpoint: the Manager endpoints require HTTPS, and the Web UI's proxy forwards to it.
ReferenceExpression apiOrigin;
if (builder.Configuration.GetValue("SocAlytics:ApiHttpsEndpoint", true))
{
    api.WithHttpsEndpoint();
    apiOrigin = ReferenceExpression.Create($"{api.GetEndpoint("https").Property(EndpointProperty.Url)}");
}
else
{
    apiOrigin = ReferenceExpression.Create($"{api.GetEndpoint("http").Property(EndpointProperty.Url)}");
}

api.WithEnvironment("Stamp__PublicBaseUri", apiOrigin);

// The simulated agent workload authenticates with an ephemeral ECDSA P-256 key generated at every start; only the public
// JWK reaches the API, so no workload credential is persisted or committed.
const string WorkloadClientId = "simulated-agent-workload";
using var workloadKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var workloadPublic = workloadKey.ExportParameters(false);
var workloadPublicJwk = JsonSerializer.Serialize(new Dictionary<string, string>
{
    ["kty"] = "EC",
    ["crv"] = "P-256",
    ["x"] = Base64Url.EncodeToString(workloadPublic.Q.X!),
    ["y"] = Base64Url.EncodeToString(workloadPublic.Q.Y!),
});
var workloadPrivateKey = Convert.ToBase64String(workloadKey.ExportPkcs8PrivateKey());

api.WithEnvironment("AgentOrchestration__Workloads__Clients__0__ClientId", WorkloadClientId)
    .WithEnvironment("AgentOrchestration__Workloads__Clients__0__PublicJwk", workloadPublicJwk)
    .WithEnvironment("AgentOrchestration__Workloads__Clients__0__Enabled", "true");

builder.AddProject<Projects.SocAlytics_Platform_SimulatedAgentWorkload>("simulated-agent-workload")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithEnvironment("SimulatedAgentWorkload__ClientId", WorkloadClientId)
    .WithEnvironment("SimulatedAgentWorkload__PrivateKey", workloadPrivateKey)
    .WithEnvironment("SimulatedAgentWorkload__ApiBaseAddress", apiOrigin)
    .WithEnvironment("SimulatedAgentWorkload__NatsUrl", natsUrl)
    .WithReference(api)
    .WaitFor(api);

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
