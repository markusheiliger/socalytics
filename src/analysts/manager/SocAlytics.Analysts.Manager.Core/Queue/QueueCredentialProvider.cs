using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.NKeys;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Queue;

/// <summary>
/// Requests queue credentials with a fresh in-memory user NKey per credential, renews them at two thirds of
/// their lifetime, and discards them on revocation, unregistration, inactivity, and exit.
/// </summary>
public sealed class QueueCredentialProvider(PlatformClient platform, TimeProvider time, ILogger<QueueCredentialProvider>? logger = null)
    : IDisposable
{
    public static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = logger ?? NullLogger<QueueCredentialProvider>.Instance;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _step = new(1, 1);
    private QueueCredential? _current;

    /// <summary>Raised with the new credential, or null when the current one was discarded.</summary>
    public event Action<QueueCredential?>? Changed;

    public QueueCredential? Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>Renews while the session source yields a session; discards the credential while it does not.</summary>
    public async Task RunAsync(
        Func<PlatformSession?> sessionSource, Func<IReadOnlyList<QueueCapability>> capabilities, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var wait = await StepAsync(sessionSource, capabilities, cancellationToken);
                await Task.Delay(wait, time, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Discard("exit");
        }
    }

    /// <summary>One evaluation: requests, renews, or discards, and returns how long to wait before the next one.</summary>
    public async Task<TimeSpan> StepAsync(
        Func<PlatformSession?> sessionSource, Func<IReadOnlyList<QueueCapability>> capabilities, CancellationToken cancellationToken)
    {
        await _step.WaitAsync(cancellationToken);
        try
        {
            var session = sessionSource();
            var offered = capabilities();
            if (session is null || offered.Count == 0)
            {
                Discard("inactive");
                return IdlePoll;
            }

            var now = time.GetUtcNow();
            if (Current is { } current)
            {
                if (now >= current.ExpiresAt)
                {
                    Discard("expired");
                }
                else if (now < current.RenewAt)
                {
                    return current.RenewAt - now;
                }
            }

            return await RequestAsync(session, offered, cancellationToken);
        }
        finally
        {
            _step.Release();
        }
    }

    public void Discard(string reason)
    {
        QueueCredential? old;
        lock (_gate)
        {
            old = _current;
            _current = null;
        }

        if (old is null)
        {
            return;
        }

        old.Dispose();
        _logger.LogInformation("Queue credential discarded: {Reason}.", reason);
        Changed?.Invoke(null);
    }

    public void Dispose()
    {
        Discard("exit");
        _step.Dispose();
    }

    private async Task<TimeSpan> RequestAsync(
        PlatformSession session, IReadOnlyList<QueueCapability> offered, CancellationToken cancellationToken)
    {
        var key = KeyPair.CreatePair(PrefixByte.User);
        WorkResult<QueueCredentialsResponse> result;
        try
        {
            result = await platform.RequestQueueCredentialsAsync(session, key.GetPublicKey(), offered, cancellationToken);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        var now = time.GetUtcNow();
        if (result is { Kind: WorkResultKind.Success, Value: { } response } && response.ExpiresAt > now)
        {
            var renewAt = now + (response.ExpiresAt - now) * 2 / 3;
            var credential = new QueueCredential(key, response, now, renewAt);
            QueueCredential? old;
            lock (_gate)
            {
                old = _current;
                _current = credential;
            }

            old?.Dispose();
            _logger.LogInformation(
                "Queue credential issued for {ConsumerCount} consumers; expires at {ExpiresAt:O}.",
                response.Consumers.Count, response.ExpiresAt);
            Changed?.Invoke(credential);
            return renewAt - now;
        }

        key.Dispose();
        if (result.Kind is WorkResultKind.Inactive or WorkResultKind.Refused)
        {
            Discard("refused");
            return IdlePoll;
        }

        _logger.LogWarning("Queue credential request did not succeed: {Kind}.", result.Kind);
        return RetryDelay;
    }
}
