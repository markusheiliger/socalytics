using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

internal sealed class NatsConnectionProvider : IAsyncDisposable
{
    private readonly NatsConnection? _connection;
    private readonly ILogger<NatsConnectionProvider> _logger;
    private readonly TransportReconnectSignal? _signal;
    private int _started;
    private int _everOpened;

    public NatsConnectionProvider(IConfiguration configuration, ILogger<NatsConnectionProvider> logger, TransportReconnectSignal? signal = null)
    {
        _logger = logger;
        _signal = signal;
        var connectionString = configuration.GetConnectionString("nats");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        _connection = new NatsConnection(NatsOpts.Default with
        {
            Url = connectionString,
            Name = "socalytics-platform",
            ConnectTimeout = TimeSpan.FromSeconds(2),
            MaxReconnectRetry = -1,
        });
        _connection.ConnectionOpened += OnOpened;
    }

    public bool IsConfigured => _connection is not null;

    public bool IsConnected => _connection?.ConnectionState == NatsConnectionState.Open;

    public NatsConnectionState ConnectionState => _connection?.ConnectionState ?? NatsConnectionState.Closed;

    public NatsConnection Connection =>
        _connection ?? throw new InvalidOperationException("NATS is not configured.");

    public bool EverOpened => Volatile.Read(ref _everOpened) == 1;

    public event Action? Reconnected;

    private ValueTask OnOpened(object? sender, NatsEventArgs args)
    {
        Interlocked.Exchange(ref _everOpened, 1);
        Reconnected?.Invoke();
        _signal?.Raise();
        return ValueTask.CompletedTask;
    }

    /// <summary>Starts the connection attempt without blocking and never throws.</summary>
    public void EnsureStarted()
    {
        if (_connection is null || Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _connection.ConnectAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                _logger.LogWarning("NATS connection attempt failed: transport-unavailable");
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class NatsConnectionStarter(NatsConnectionProvider provider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        provider.EnsureStarted();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
