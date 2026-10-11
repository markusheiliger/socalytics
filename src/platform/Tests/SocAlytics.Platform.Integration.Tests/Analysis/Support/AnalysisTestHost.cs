using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Support;

internal enum ManagerAuthentication
{
    Test,
    Real,
}

internal sealed class AnalysisTestHost : IAsyncDisposable
{
    private readonly IsolatedDatabase _database;
    private readonly NatsContainerFixture _nats;
    private readonly RustFsContainerFixture _rustFs;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly string _keyPrefix = RecordingTestData.NewKeyPrefix();
    private PlatformApiFactory? _factory;

    public AnalysisTestHost(
        IsolatedDatabase database,
        NatsContainerFixture nats,
        RustFsContainerFixture rustFs,
        bool workersEnabled = false,
        FakeTimeProvider? time = null,
        Action<IServiceCollection>? configureServices = null,
        ManagerAuthentication managerAuthentication = ManagerAuthentication.Test)
    {
        ManagerAuthentication = managerAuthentication;
        _database = database;
        _nats = nats;
        _rustFs = rustFs;
        _configureServices = configureServices;
        WorkersEnabled = workersEnabled;
        Time = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    }

    public ManagerAuthentication ManagerAuthentication { get; }

    public bool WorkersEnabled { get; set; }

    public FakeTimeProvider Time { get; }

    public PlatformApiFactory Factory => _factory ?? throw new InvalidOperationException("The host is not started.");

    public IServiceProvider Services => Factory.Services;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _factory = new PlatformApiFactory(
            _database,
            clubBootstrap: new Dictionary<string, string?>
            {
                ["ClubDisplayName"] = "Analysis Club",
                ["FirstClubAdmin:AccountName"] = "analysis-admin",
                ["FirstClubAdmin:InitialPassword"] = "Analysis-Initial-Pass-1234",
            },
            recordingUpload: RecordingTestData.WithKeyPrefix(_keyPrefix),
            objectStorage: new Dictionary<string, string?>
            {
                ["ServiceUrl"] = _rustFs.ServiceUrl,
                ["Region"] = _rustFs.Region,
                ["AccessKey"] = _rustFs.AccessKey,
                ["SecretKey"] = _rustFs.SecretKey,
                ["Bucket"] = _rustFs.Bucket,
            },
            configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time));
                if (ManagerAuthentication == ManagerAuthentication.Test)
                {
                    services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
                    {
                        var scheme = o.Schemes.Single(x => x.Name == AnalystManagerDPoPDefaults.AuthenticationScheme);
                        scheme.HandlerType = typeof(TestManagerAuthenticationHandler);
                    });
                }
                else
                {
                    services.Configure<SocAlytics.Platform.Application.Registry.RegistryOptions>(o =>
                    {
                        o.AllowSoftwareKeys = true;
                        o.RateLimits.PairingCreationPerMinute = 10_000;
                        o.RateLimits.StatusPerMinute = 10_000;
                        o.RateLimits.ActivationPerMinute = 10_000;
                        o.RateLimits.CodeSubmissionPerMinute = 10_000;
                    });
                }

                _configureServices?.Invoke(services);
            },
            extraSettings: new Dictionary<string, string?>
            {
                ["ConnectionStrings:socalytics"] = _database.AppConnectionString,
                ["ConnectionStrings:nats"] = _nats.ConnectionString,
                ["Messaging:Nats:ProvisionTopology"] = "true",
                ["Analysis:Workers:Enabled"] = WorkersEnabled ? "true" : "false",
            });
        await _factory.WaitUntilHealthyAsync(cancellationToken);
    }

    /// <summary>Disposes the host and starts a new one against the same database, bucket, and NATS container.</summary>
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }

        await StartAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(string Subject, string? MessageId, byte[] Data)>> ReadStreamAsync(
        string stream, CancellationToken cancellationToken)
    {
        await using var connection = new NatsConnection(NatsOpts.Default with { Url = _nats.ConnectionString });
        var js = new NatsJSContext(connection);
        var info = await js.GetStreamAsync(stream, cancellationToken: cancellationToken);
        var result = new List<(string, string?, byte[])>();
        for (var seq = info.Info.State.FirstSeq; seq <= info.Info.State.LastSeq && info.Info.State.Messages > 0; seq++)
        {
            try
            {
                var raw = await info.GetAsync(new StreamMsgGetRequest { Seq = seq }, cancellationToken);
                string? id = null;
                if (!string.IsNullOrEmpty(raw.Message.Hdrs))
                {
                    var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw.Message.Hdrs));
                    var line = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("Nats-Msg-Id:", StringComparison.OrdinalIgnoreCase));
                    id = line?["Nats-Msg-Id:".Length..].Trim();
                }

                result.Add((raw.Message.Subject, id, raw.Message.Data.ToArray()));
            }
            catch (NatsJSApiException)
            {
            }
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }
    }
}
