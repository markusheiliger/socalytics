using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Queue;

/// <summary>One fetched delivery; acknowledging and negative acknowledging are the only ways to settle it.</summary>
public interface IQueueDelivery
{
    string Capability { get; }

    ReadOnlyMemory<byte> Data { get; }

    Task AckAsync(CancellationToken cancellationToken);

    Task NakAsync(TimeSpan delay, CancellationToken cancellationToken);

    Task NakAsync(CancellationToken cancellationToken);
}

/// <summary>The queue operations the Manager's acquisition needs; faked in tests.</summary>
public interface IQueueConnection : IAsyncDisposable
{
    /// <summary>Fetches at most one delivery from the capability's consumer; null when none is available within <paramref name="expires"/>.</summary>
    Task<IQueueDelivery?> FetchOneAsync(string capability, TimeSpan expires, CancellationToken cancellationToken);
}

/// <summary>Connection to the queue with one issued credential: user JWT, nonce signing, and the Manager's inbox prefix.</summary>
public sealed class QueueConnection : IQueueConnection
{
    private readonly NatsConnection _connection;
    private readonly NatsJSContext _js;
    private readonly string _stream;
    private readonly IReadOnlyDictionary<string, string> _consumers;

    private QueueConnection(QueueCredential credential, Uri url)
    {
        _stream = credential.Stream;
        _consumers = credential.Consumers.ToDictionary(c => c.Capability, c => c.Consumer, StringComparer.Ordinal);
        var jwt = credential.UserJwt;
        var seed = credential.Seed;
        _connection = new NatsConnection(NatsOpts.Default with
        {
            Url = url.OriginalString,
            Name = "socalytics-analyst-manager",
            InboxPrefix = credential.InboxPrefix,
            AuthOpts = new NatsAuthOpts { Jwt = jwt, Seed = seed },
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxReconnectRetry = -1,
        });
        _js = new NatsJSContext(_connection);
    }

    public static bool IsAllowedScheme(string queueUrl, IHostEnvironment? environment)
    {
        if (!Uri.TryCreate(queueUrl, UriKind.Absolute, out var url))
        {
            return false;
        }

        return url.Scheme switch
        {
            "tls" => true,
            "nats" => environment is not null && (environment.IsDevelopment() || environment.IsEnvironment("Test")),
            _ => false,
        };
    }

    /// <summary>Connects with the credential; refuses <c>nats://</c> outside Development and Test and requires <c>tls://</c> there.</summary>
    public static async Task<QueueConnection> ConnectAsync(
        QueueCredential credential, IHostEnvironment? environment, CancellationToken cancellationToken)
    {
        if (!IsAllowedScheme(credential.QueueUrl, environment))
        {
            throw new PlatformException(ManagerDiagnosticCodes.InsecureEndpoint);
        }

        var connection = new QueueConnection(credential, new Uri(credential.QueueUrl));
        try
        {
            await connection._connection.ConnectAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<IQueueDelivery?> FetchOneAsync(string capability, TimeSpan expires, CancellationToken cancellationToken)
    {
        if (!_consumers.TryGetValue(capability, out var name))
        {
            return null;
        }

        var consumer = await _js.GetConsumerAsync(_stream, name, cancellationToken);
        await foreach (var message in consumer.FetchAsync(
            new NatsJSFetchOpts { MaxMsgs = 1, Expires = expires }, NatsRawSerializer<byte[]>.Default, cancellationToken))
        {
            return new Delivery(capability, message);
        }

        return null;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private sealed class Delivery(string capability, INatsJSMsg<byte[]> message) : IQueueDelivery
    {
        public string Capability => capability;

        public ReadOnlyMemory<byte> Data => message.Data ?? [];

        public async Task AckAsync(CancellationToken cancellationToken) => await message.AckAsync(cancellationToken: cancellationToken);

        public async Task NakAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            await message.NakAsync(delay, cancellationToken: cancellationToken);

        public async Task NakAsync(CancellationToken cancellationToken) =>
            await message.NakAsync(cancellationToken: cancellationToken);
    }
}

/// <summary>Follows the provider: reconnects with each renewed credential and holds no connection while none is issued.</summary>
public sealed class RenewingQueueConnection : IQueueConnection
{
    private readonly QueueCredentialProvider _provider;
    private readonly IHostEnvironment? _environment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private QueueConnection? _connection;
    private QueueCredential? _connectedWith;

    public RenewingQueueConnection(QueueCredentialProvider provider, IHostEnvironment? environment)
    {
        _provider = provider;
        _environment = environment;
        _provider.Changed += OnChanged;
    }

    public async Task<IQueueDelivery?> FetchOneAsync(string capability, TimeSpan expires, CancellationToken cancellationToken)
    {
        QueueConnection connection;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var credential = _provider.Current;
            if (credential is null)
            {
                return null;
            }

            if (!ReferenceEquals(credential, _connectedWith))
            {
                var old = _connection;
                _connection = null;
                _connectedWith = null;
                if (old is not null)
                {
                    await old.DisposeAsync();
                }

                _connection = await QueueConnection.ConnectAsync(credential, _environment, cancellationToken);
                _connectedWith = credential;
            }

            connection = _connection!;
        }
        finally
        {
            _gate.Release();
        }

        return await connection.FetchOneAsync(capability, expires, cancellationToken);
    }

    private void OnChanged(QueueCredential? credential)
    {
        if (credential is not null)
        {
            return;
        }

        // A discarded credential closes the connection so a revoked Manager stops pulling at once.
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                var old = _connection;
                _connection = null;
                _connectedWith = null;
                if (old is not null)
                {
                    await old.DisposeAsync();
                }
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Changed -= OnChanged;
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
