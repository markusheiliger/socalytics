using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Images.Tests.Support;

/// <summary>
/// The dependencies of one stamp on a dedicated network (PostgreSQL, NATS with JWT trust, RustFS): the schema is prepared by the
/// Migrator image, a default analysis workflow is seeded, and the complete production option set for the API container is exposed.
/// </summary>
public sealed class StampDependenciesFixture(ImageBuildFixture images) : IAsyncLifetime
{
    private const string MigratorDockerfile = "src/platform/SocAlytics.Platform.Migrator/Dockerfile";
    private const string Bucket = "socalytics-recordings";
    private const string StorageRegion = "us-east-1";

    private readonly ContainerProbe _probe = new();
    private INetwork? _network;
    private PostgreSqlContainer? _postgres;
    private IContainer? _nats;
    private IContainer? _rustfs;
    private string _appPassword = string.Empty;
    private string _migratorPassword = string.Empty;
    private string _natsOperatorSeed = string.Empty;
    private string _natsAccountSeed = string.Empty;
    private string _natsSystemAccountSeed = string.Empty;
    private string _storageAccessKey = string.Empty;
    private string _storageSecretKey = string.Empty;
    private string _adminPassword = string.Empty;

    internal ContainerProbe Probe => _probe;

    public ImageBuildFixture Images => images;

    public string NetworkName => _network?.Name ?? throw new InvalidOperationException("The stamp dependencies have not been started.");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _migratorPassword = Secret();
        _appPassword = Secret();
        _storageAccessKey = Secret();
        _storageSecretKey = Secret();
        _adminPassword = Secret();
        (_natsOperatorSeed, _natsAccountSeed, _natsSystemAccountSeed) = NatsTrust.GenerateSeeds();

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync(ct);

        await StartPostgresAsync(ct);
        await StartNatsAsync(ct);
        await StartRustFsAsync(ct);

        // The schema comes from the Migrator image, so the API image is verified against what the platform deploys.
        var migrator = await images.BuildAsync(MigratorDockerfile);
        var run = await _probe.RunToCompletionAsync(
            migrator,
            NetworkName,
            new Dictionary<string, string> { ["ConnectionStrings__socalytics-migrator"] = DatabaseConnectionString("postgres", 5432, "socalytics_migrator", _migratorPassword) },
            ct: ct);
        run.ExitCode.ShouldBe(0, run.Logs);

        await DefaultWorkflowArrangement.SeedAsync(ApplicationConnectionString(), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _probe.Dispose();
        foreach (var container in new IAsyncDisposable?[] { _rustfs, _nats, _postgres })
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }

        if (_network is not null)
        {
            await _network.DisposeAsync();
        }
    }

    /// <summary>The production option set of the API container as environment variables; a fresh copy per call.</summary>
    /// <param name="databaseHost">Replaces the database host, for example with an unreachable address.</param>
    public Dictionary<string, string> ApiEnvironment(string databaseHost = "postgres") =>
        new()
        {
            ["ConnectionStrings__socalytics"] = DatabaseConnectionString(databaseHost, 5432, "socalytics_app", _appPassword),
            ["ConnectionStrings__nats"] = "nats://nats:4222",
            ["Nats__Authentication__Mode"] = "Jwt",
            ["Nats__Authentication__OperatorSeed"] = _natsOperatorSeed,
            ["Nats__Authentication__AccountSeed"] = _natsAccountSeed,
            ["Nats__Authentication__SystemAccountSeed"] = _natsSystemAccountSeed,
            ["Messaging__Nats__ProvisionTopology"] = "true",
            ["AnalystWork__QueueUrl"] = "nats://nats:4222",
            ["ObjectStorage__ServiceUrl"] = "http://rustfs:9000",
            ["ObjectStorage__AccessKey"] = _storageAccessKey,
            ["ObjectStorage__SecretKey"] = _storageSecretKey,
            ["ObjectStorage__Bucket"] = Bucket,
            ["ObjectStorage__Region"] = StorageRegion,
            ["Recordings__Upload__SessionLifetime"] = "00:30:00",
            ["Recordings__Upload__GrantLifetime"] = "00:15:00",
            ["Recordings__Upload__ExpirySweepInterval"] = "00:01:00",
            ["Recordings__Upload__MaxObjectSizeBytes"] = "5497558138880",
            ["Recordings__Upload__MinPartSizeBytes"] = "5242880",
            ["Recordings__Upload__MaxPartSizeBytes"] = "5368709120",
            ["Recordings__Upload__MaxPartCount"] = "10000",
            ["Recordings__Upload__MaxGrantsPerRequest"] = "1000",
            ["Recordings__Upload__AllowedContentTypes__0"] = "video/mp4",
            ["Recordings__Upload__AllowedContentTypes__1"] = "video/quicktime",
            // Never contacted: the delivery worker only connects when a message is queued.
            ["Email__Smtp__Host"] = "smtp.invalid",
            ["Email__Smtp__Port"] = "587",
            ["Email__Smtp__Security"] = "StartTls",
            ["Email__SenderAddress"] = "socalytics@example.org",
            ["Email__SenderName"] = "SocAlytics",
            ["Email__Confirmation__Lifetime"] = "1.00:00:00",
            ["Email__Confirmation__MaxPerHour"] = "3",
            ["Email__Confirmation__AttemptsPerMinute"] = "10",
            ["Email__Delivery__MaxAttempts"] = "8",
            ["Email__Delivery__PollInterval"] = "00:00:05",
            ["Email__Delivery__LeaseDuration"] = "00:02:00",
            ["Notifications__CoalescingWindow"] = "00:15:00",
            ["Notifications__WebUiBaseUri"] = "https://socalytics.example.org",
            ["Analysis__Results__GrantLifetime"] = "00:10:00",
            ["Analysis__Results__MaxObjectsPerAttempt"] = "16",
            ["Analysis__Results__MaxObjectBytes"] = "104857600",
            ["Analysis__Results__MaxAttemptBytes"] = "1073741824",
            ["Analysis__Results__MaxManifestBytes"] = "1048576",
            ["Analysis__Results__MaxFactArtifactBytes"] = "16777216",
            ["Analysis__Results__MaxFactPayloadBytes"] = "16384",
            ["Analysis__Results__MaxFactsPerResult"] = "50000",
            ["Analysis__Facts__DefaultPageSize"] = "100",
            ["Analysis__Facts__MaxPageSize"] = "500",
            ["Analysis__Reads__GrantLifetime"] = "00:10:00",
            ["Analysis__Reads__MaxPageSize"] = "100",
            ["Analysis__Evidence__GrantLifetime"] = "00:05:00",
            ["Analysis__Evidence__RetryAfter"] = "00:00:05",
            ["Analysis__Evidence__ResolutionsPerMinute"] = "60",
            ["Analysis__Evidence__MaxDuration"] = "00:10:00",
            ["Analysis__Evidence__MaxContext"] = "00:00:10",
            ["Stamp__Id"] = "image-evidence-stamp",
            ["Stamp__PublicBaseUri"] = "https://socalytics.example.org",
            ["Registry__Pairing__VerificationBaseUri"] = "https://socalytics.example.org",
            ["ClubBootstrap__ClubDisplayName"] = "Image Evidence Club",
            ["ClubBootstrap__FirstClubAdmin__AccountName"] = "club-admin",
            ["ClubBootstrap__FirstClubAdmin__InitialPassword"] = _adminPassword,
            ["IdentityAccess__Session__IdleTimeout"] = "00:30:00",
            ["IdentityAccess__Session__AbsoluteLifetime"] = "08:00:00",
            ["IdentityAccess__Lockout__MaxFailedAccessAttempts"] = "5",
            ["IdentityAccess__Lockout__LockoutDuration"] = "00:15:00",
            ["IdentityAccess__Password__RequiredLength"] = "12",
            ["IdentityAccess__OneTimeCredential__Lifetime"] = "1.00:00:00",
            ["WebUi__MinimumVersion"] = ImageBuildFixture.TestVersion,
        };

    private string ApplicationConnectionString() =>
        new NpgsqlConnectionStringBuilder(_postgres!.GetConnectionString())
        {
            Database = "socalytics",
            Username = "socalytics_app",
            Password = _appPassword,
            Pooling = false,
        }.ConnectionString;

    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string DatabaseConnectionString(string host, int port, string user, string password) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = "socalytics",
            Username = user,
            Password = password,
            Pooling = false,
        }.ConnectionString;

    private async Task StartPostgresAsync(CancellationToken ct)
    {
        var initScript = Path.Combine(AppContext.BaseDirectory, "PostgresInit", "01-socalytics-roles.sh");
        _postgres = new PostgreSqlBuilder("postgres:18")
            .WithDatabase("socalytics")
            .WithEnvironment("SOCALYTICS_MIGRATOR_PASSWORD", _migratorPassword)
            .WithEnvironment("SOCALYTICS_APP_PASSWORD", _appPassword)
            .WithResourceMapping(new FileInfo(initScript), "/docker-entrypoint-initdb.d/")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .Build();
        await _postgres.StartAsync(ct);
    }

    private async Task StartNatsAsync(CancellationToken ct)
    {
        var trust = new NatsTrust(Options.Create(new NatsAuthenticationOptions
        {
            Mode = NatsAuthenticationMode.Jwt,
            OperatorSeed = _natsOperatorSeed,
            AccountSeed = _natsAccountSeed,
            SystemAccountSeed = _natsSystemAccountSeed,
        }));

        var config = $$"""
            port: 4222
            server_name: socalytics-image-evidence
            operator: {{trust.BuildOperatorJwt()}}
            system_account: {{trust.SystemAccountPublicKey}}
            resolver: {
              type: full
              dir: '/tmp/nats-resolver'
              allow_delete: false
              interval: "2m"
            }
            resolver_preload: {
              {{trust.StampAccountPublicKey}}: {{trust.BuildStampAccountJwt()}}
              {{trust.SystemAccountPublicKey}}: {{trust.BuildSystemAccountJwt()}}
            }
            jetstream: {
              store_dir: '/tmp/nats-js'
            }
            """;

        _nats = new ContainerBuilder("nats:2.14")
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/etc/nats/nats.conf")
            .WithCommand("-c", "/etc/nats/nats.conf")
            .WithNetwork(_network)
            .WithNetworkAliases("nats")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Server is ready"))
            .Build();
        await _nats.StartAsync(ct);
    }

    private async Task StartRustFsAsync(CancellationToken ct)
    {
        _rustfs = new ContainerBuilder("rustfs/rustfs:1.0.1")
            .WithPortBinding(9000, true)
            .WithEnvironment("RUSTFS_ACCESS_KEY", _storageAccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", _storageSecretKey)
            .WithNetwork(_network)
            .WithNetworkAliases("rustfs")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/health").ForPort(9000)))
            .Build();
        await _rustfs.StartAsync(ct);

        using var client = new AmazonS3Client(
            new BasicAWSCredentials(_storageAccessKey, _storageSecretKey),
            new AmazonS3Config
            {
                ServiceURL = $"http://{_rustfs.Hostname}:{_rustfs.GetMappedPublicPort(9000)}",
                ForcePathStyle = true,
                AuthenticationRegion = StorageRegion,
                UseHttp = true,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
        await client.PutBucketAsync(Bucket, ct);
    }
}
