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
    private readonly SoftHsmToken _token;
    private readonly string _stateDirectory;
    private IHost? _manager;

    private EndToEndHarness(
        PostgresContainerFixture postgres, NatsJwtContainerFixture nats, SoftHsmToken token, string stateDirectory,
        IsolatedDatabase database, PlatformApiFactory platform, ApiSession admin)
    {
        _postgres = postgres;
        _nats = nats;
        _token = token;
        _stateDirectory = stateDirectory;
        Database = database;
        Platform = platform;
        Admin = admin;
    }

    public IsolatedDatabase Database { get; }

    public PlatformApiFactory Platform { get; }

    public ApiSession Admin { get; }

    public CapturingLoggerProvider ManagerLogs { get; } = new();

    public IHost Manager => _manager ?? throw new InvalidOperationException("The Manager has not been started.");

    public IManagerControl Control => Manager.Services.GetRequiredService<IManagerControl>();

    /// <summary>Starts the platform; returns null when SoftHSM2 (needed for the Manager's device key) is unavailable.</summary>
    public static async Task<EndToEndHarness?> StartAsync(CancellationToken ct)
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

        var database = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(database, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);

        var settings = new Dictionary<string, string?>(nats.AuthenticationSettings)
        {
            ["ConnectionStrings:nats"] = nats.ConnectionString,
            ["Messaging:Nats:ProvisionTopology"] = "true",
            ["Analysis:Workers:Enabled"] = "true",
            ["AnalystWork:QueueUrl"] = nats.ConnectionString,
        };
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
                    o.AllowSoftwareKeys = true;
                    o.RateLimits.PairingCreationPerMinute = 10_000;
                    o.RateLimits.StatusPerMinute = 10_000;
                    o.RateLimits.ActivationPerMinute = 10_000;
                    o.RateLimits.CodeSubmissionPerMinute = 10_000;
                });
            },
            extraSettings: settings);
        await platform.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(database, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(platform, "admin-1", TestMembers.DefaultPassword, ct);
        return new EndToEndHarness(postgres, nats, token, token.NewStateDirectory(), database, platform, admin);
    }

    /// <summary>Starts the Manager, pairs it, approves it as the registrar and waits until it is active with a passed preflight.</summary>
    public async Task StartManagerAsync(CancellationToken ct)
    {
        var builder = ManagerHost.CreateBuilder(["--environment", "Test"]);
        var configuration = new Dictionary<string, string?>
        {
            ["Manager:StateDirectory"] = _stateDirectory,
            ["Runtime:Profile"] = SimulatedRuntimeAdapter.TestProfile,
            ["Runtime:Simulated:MaxParallelAttempts"] = "2",
        };
        foreach (var capability in new[] { SegmentCapability, MatchCapability })
        {
            configuration[$"Runtime:Simulated:Capabilities:{capability}:Version"] = "1";
            configuration[$"Runtime:Simulated:Capabilities:{capability}:RunDuration"] = RunDuration.ToString();
        }

        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.AddProvider(ManagerLogs);
        builder.Services.RemoveAll<IDeviceKeyProvider>();
        builder.Services.AddSingleton<IDeviceKeyProvider>(
            Pkcs11KeyProvider.CreateForTests(_token.ModulePath, SoftHsmToken.TokenLabel, _stateDirectory));
        builder.Services.RemoveAll<RuntimeCompatibilityList>();
        builder.Services.AddSingleton(SimulatedRuntimeAdapter.List());
        builder.Services.AddHttpClient<PlatformClient>().ConfigurePrimaryHttpMessageHandler(() => Platform.Server.CreateHandler());
        _manager = builder.Build();
        await _manager.StartAsync(ct);

        var registered = await Control.RegisterAsync(new RegisterRequest("https://localhost", HostLabel: "e2e-manager"), ct);
        registered.Succeeded.ShouldBeTrue(registered.Code);

        using var submission = await Admin.SendAsync(
            HttpMethod.Post, "/api/v1/analyst-manager-registrations",
            new { pairingCode = registered.PairingCode, deviceFingerprint = registered.DeviceFingerprint }, ct);
        submission.EnsureSuccessStatusCode();
        var registrationId = (await submission.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("registrationId").GetGuid();
        using var approval = await Admin.SendAsync(HttpMethod.Post, $"/api/v1/analyst-manager-registrations/{registrationId}/approval", null, ct);
        approval.EnsureSuccessStatusCode();

        var scheduler = Manager.Services.GetRequiredService<PreflightScheduler>();
        await WaitAsync(
            () => Control.GetStatus().RegistrationState == "active" && scheduler.Latest?.Passed == true,
            TimeSpan.FromSeconds(60), "The Manager did not become active with a passed preflight.", ct);
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
        if (_manager is not null)
        {
            await _manager.StopAsync(CancellationToken.None);
            _manager.Dispose();
        }

        Admin.Dispose();
        await Platform.DisposeAsync();
        await Database.DisposeAsync();
        await _nats.DisposeAsync();
        await _postgres.DisposeAsync();
        _token.Dispose();
        try
        {
            Directory.Delete(_stateDirectory, true);
        }
        catch (IOException)
        {
        }
    }
}
