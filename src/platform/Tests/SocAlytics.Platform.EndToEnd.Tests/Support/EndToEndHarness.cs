using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;

namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>
/// The in-process platform API on real PostgreSQL and JWT-authenticated NATS, plus a real Manager Core host
/// (simulated runtime, software PKCS#11 key) that talks to the API through the test server.
/// </summary>
internal sealed class EndToEndHarness : IAsyncDisposable
{
    public const string SegmentCapability = "dev-segment-capability";
    public const string MatchCapability = "dev-match-capability";
    public static readonly TimeSpan RunDuration = TimeSpan.FromSeconds(3);

    private readonly PostgresContainerFixture _postgres;
    private readonly NatsJwtContainerFixture _nats;
    private readonly RustFsContainerFixture _store;
    private readonly SoftHsmToken _token;
    private readonly string _stateDirectory;
    private readonly List<ManagerInstance> _managers = [];

    private EndToEndHarness(
        PostgresContainerFixture postgres, NatsJwtContainerFixture nats, RustFsContainerFixture store, SoftHsmToken token, string stateDirectory,
        IsolatedDatabase database, PlatformApiFactory platform, ApiSession admin)
    {
        _postgres = postgres;
        _nats = nats;
        _store = store;
        _token = token;
        _stateDirectory = stateDirectory;
        Database = database;
        Platform = platform;
        Admin = admin;
    }

    public RustFsContainerFixture Store => _store;

    public IsolatedDatabase Database { get; }

    public PlatformApiFactory Platform { get; }

    public ApiSession Admin { get; }

    public CapturingLoggerProvider ManagerLogs { get; } = new();

    public IHost Manager => _managers.LastOrDefault()?.Host ?? throw new InvalidOperationException("The Manager has not been started.");

    public IManagerControl Control => Manager.Services.GetRequiredService<IManagerControl>();

    /// <summary>Starts the platform; returns null when SoftHSM2 (needed for the Manager's device key) is unavailable.</summary>
    public static async Task<EndToEndHarness?> StartAsync(CancellationToken ct, IReadOnlyDictionary<string, string?>? registrySettings = null)
    {
        var token = SoftHsmToken.TryCreate();
        if (token is null)
        {
            return null;
        }

        var postgres = new PostgresContainerFixture();
        var nats = new NatsJwtContainerFixture();
        await postgres.InitializeAsync();
        await nats.InitializeAsync();
        var store = new RustFsContainerFixture();
        await store.InitializeAsync();

        var database = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(database, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);

        var settings = new Dictionary<string, string?>(nats.AuthenticationSettings)
        {
            ["ConnectionStrings:nats"] = nats.ConnectionString,
            ["Messaging:Nats:ProvisionTopology"] = "true",
            ["Analysis:Workers:Enabled"] = "true",
            ["AnalystWork:QueueUrl"] = nats.ConnectionString,
            ["Analysis:Leases:MonitorIntervalSeconds"] = "1",
            ["ObjectStorage:ServiceUrl"] = store.ServiceUrl,
            ["ObjectStorage:Region"] = store.Region,
            ["ObjectStorage:AccessKey"] = store.AccessKey,
            ["ObjectStorage:SecretKey"] = store.SecretKey,
            ["ObjectStorage:Bucket"] = store.Bucket,
        };
        foreach (var (key, value) in registrySettings ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var platform = new PlatformApiFactory(
            database,
            new Dictionary<string, string?>
            {
                ["ClubDisplayName"] = "End To End Club",
                ["FirstClubAdmin:AccountName"] = "e2e-admin",
                ["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
            },
            configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(TimeProvider.System));
                services.Configure<RegistryOptions>(o =>
                {
                    o.SoftwareKeys = SoftwareKeyPolicy.All;
                    o.RateLimits.PairingCreationPerMinute = 10_000;
                    o.RateLimits.StatusPerMinute = 10_000;
                    o.RateLimits.ActivationPerMinute = 10_000;
                    o.RateLimits.CodeSubmissionPerMinute = 10_000;
                });
            },
            extraSettings: settings,
            omitWorkflow: registrySettings is not null);
        if (registrySettings is null)
        {
            await platform.WaitUntilHealthyAsync(ct);
        }
        else
        {
            await platform.WaitUntilHealthyExceptWorkflowAsync(ct);
        }

        await TestMembers.SeedAsync(database, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(platform, "admin-1", TestMembers.DefaultPassword, ct);
        return new EndToEndHarness(postgres, nats, store, token, token.NewStateDirectory(), database, platform, admin);
    }

    /// <summary>
    /// Starts a Manager, pairs it, approves it as the registrar and waits until it is active with a passed preflight.
    /// Every call starts another Manager host with its own device key and state.
    /// </summary>
    public async Task<IHost> StartManagerAsync(
        CancellationToken ct, TimeSpan? runDuration = null, SimulatedOutcome outcome = SimulatedOutcome.Succeed,
        string resultMode = SimulatedResultModes.Valid, string? dockerCompatibilityListPath = null, IHost? restartOf = null)
    {
        // A restart reuses the state directory and device key of a stopped Manager, so it restores that registration.
        var previous = restartOf is null ? null : _managers.Single(m => ReferenceEquals(m.Host, restartOf));
        var stateDirectory = previous?.StateDirectory ?? (_managers.Count == 0 ? _stateDirectory : _token.NewStateDirectory());
        var connection = new ConnectionSwitch();
        var builder = ManagerHost.CreateBuilder(["--environment", "Test"]);
        var configuration = new Dictionary<string, string?>
        {
            ["Manager:StateDirectory"] = stateDirectory,
            ["Runtime:Profile"] = SimulatedRuntimeAdapter.TestProfile,
            ["Runtime:Simulated:MaxParallelAttempts"] = "2",
        };
        if (dockerCompatibilityListPath is null)
        {
            foreach (var capability in new[] { SegmentCapability, MatchCapability })
            {
                configuration[$"Runtime:Simulated:Capabilities:{capability}:Version"] = "1.0.0";
                configuration[$"Runtime:Simulated:Capabilities:{capability}:RunDuration"] = (runDuration ?? RunDuration).ToString();
                configuration[$"Runtime:Simulated:Capabilities:{capability}:Outcome"] = outcome.ToString();
                configuration[$"Runtime:Simulated:Capabilities:{capability}:ResultMode"] = resultMode;
            }
        }
        else
        {
            configuration["Runtime:Profile"] = "docker-desktop";
            configuration["Runtime:Docker:Endpoint"] = "unix:///var/run/docker.sock";
            configuration["Runtime:CompatibilityListPath"] = dockerCompatibilityListPath;
        }

        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.AddProvider(ManagerLogs);
        builder.Services.RemoveAll<IDeviceKeyProvider>();
        builder.Services.AddSingleton<IDeviceKeyProvider>(
            Pkcs11KeyProvider.CreateForTests(_token.ModulePath, SoftHsmToken.TokenLabel, stateDirectory));
        if (dockerCompatibilityListPath is null)
        {
            builder.Services.RemoveAll<RuntimeCompatibilityList>();
            builder.Services.AddSingleton(SimulatedRuntimeAdapter.List());
        }
        builder.Services.AddHttpClient<PlatformClient>().ConfigurePrimaryHttpMessageHandler(() =>
        {
            connection.InnerHandler = Platform.Server.CreateHandler();
            return connection;
        });
        var host = builder.Build();
        var instance = new ManagerInstance(host, connection, stateDirectory);
        _managers.Add(instance);
        await host.StartAsync(ct);

        var control = host.Services.GetRequiredService<IManagerControl>();
        if (previous is not null)
        {
            instance.RegistrationId = previous.RegistrationId;
            var restoredScheduler = host.Services.GetRequiredService<PreflightScheduler>();
            await WaitAsync(
                () => control.GetStatus().RegistrationState == "active" && restoredScheduler.Latest?.Passed == true,
                TimeSpan.FromSeconds(60), "The restarted Manager did not become active with a passed preflight.", ct);
            return host;
        }

        var registered = await control.RegisterAsync(new RegisterRequest("https://localhost", HostLabel: $"e2e-manager-{_managers.Count}"), ct);
        registered.Succeeded.ShouldBeTrue(registered.Code);

        using var submission = await Admin.SendAsync(
            HttpMethod.Post, "/api/v1/analyst-manager-registrations",
            new { pairingCode = registered.PairingCode, deviceFingerprint = registered.DeviceFingerprint }, ct);
        submission.EnsureSuccessStatusCode();
        var registrationId = (await submission.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("registrationId").GetGuid();
        using var approval = await Admin.SendAsync(HttpMethod.Post, $"/api/v1/analyst-manager-registrations/{registrationId}/approval", null, ct);
        approval.EnsureSuccessStatusCode();
        instance.RegistrationId = registrationId;

        var scheduler = host.Services.GetRequiredService<PreflightScheduler>();
        await WaitAsync(
            () => control.GetStatus().RegistrationState == "active" && scheduler.Latest?.Passed == true,
            TimeSpan.FromSeconds(60), "The Manager did not become active with a passed preflight.", ct);
        return host;
    }

    /// <summary>
    /// Simulates a crash: the Manager can no longer reach the platform (so it cannot report anything), then its host stops.
    /// The platform only learns about it through the lease expiry.
    /// </summary>
    public async Task KillManagerAsync(IHost host)
    {
        var instance = _managers.Single(m => ReferenceEquals(m.Host, host));
        instance.Connection.Down = true;
        await host.StopAsync(CancellationToken.None);
        instance.Stopped = true;
    }

    /// <summary>Revokes the Manager's registration as the Club Admin.</summary>
    public async Task RevokeManagerAsync(IHost host, CancellationToken ct)
    {
        var instance = _managers.Single(m => ReferenceEquals(m.Host, host));
        using var revoke = await Admin.SendAsync(
            HttpMethod.Post, $"/api/v1/analyst-manager-registrations/{instance.RegistrationId}/revocation", new { reason = "device lost" }, ct);
        revoke.EnsureSuccessStatusCode();
    }

    public static async Task WaitAsync(Func<bool> condition, TimeSpan timeout, string failure, CancellationToken ct)
    {
        await WaitAsync(() => Task.FromResult(condition()), timeout, failure, ct);
    }

    public static async Task WaitAsync(Func<Task<bool>> condition, TimeSpan timeout, string failure, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(failure);
            }

            await Task.Delay(250, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _managers)
        {
            if (!instance.Stopped)
            {
                await instance.Host.StopAsync(CancellationToken.None);
            }

            instance.Host.Dispose();
        }

        Admin.Dispose();
        await Platform.DisposeAsync();
        await Database.DisposeAsync();
        await _nats.DisposeAsync();
        await _store.DisposeAsync();
        await _postgres.DisposeAsync();
        _token.Dispose();
        foreach (var directory in _managers.Select(m => m.StateDirectory).Append(_stateDirectory).Distinct())
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class ConnectionSwitch : DelegatingHandler
    {
        public volatile bool Down;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Down ? throw new HttpRequestException("The platform is unreachable.") : base.SendAsync(request, cancellationToken);
    }

    private sealed class ManagerInstance(IHost host, ConnectionSwitch connection, string stateDirectory)
    {
        public IHost Host { get; } = host;

        public ConnectionSwitch Connection { get; } = connection;

        public string StateDirectory { get; } = stateDirectory;

        public bool Stopped { get; set; }

        public Guid RegistrationId { get; set; }
    }
}
