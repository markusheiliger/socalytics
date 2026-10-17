using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Runtime.Docker;

/// <summary>A registry pull credential; the token is a credential and is never logged.</summary>
public sealed record RegistryPullCredential(
    string Registry, IReadOnlyList<string> Repositories, string Kind, string? Token, DateTimeOffset ExpiresAt, DateTimeOffset RenewAt)
{
    /// <summary>The token for <c>X-Registry-Auth</c>; null for an anonymous credential.</summary>
    public string? AuthToken => Kind == "bearer" ? Token : null;

    public override string ToString() => $"RegistryPullCredential {{ Registry = {Registry}, Kind = {Kind} }}";
}

/// <summary>
/// Requests registry pull credentials from the platform, keeps them in memory only, renews them at 80% of their
/// lifetime, and discards them on revocation, unregistration, inactivity, refusal, and exit.
/// </summary>
public sealed class RegistryCredentialProvider(PlatformClient platform, TimeProvider time, ILogger<RegistryCredentialProvider>? logger = null)
    : IDisposable
{
    public static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = logger ?? NullLogger<RegistryCredentialProvider>.Instance;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _step = new(1, 1);
    private RegistryPullCredential? _current;

    /// <summary>Raised with the new credential, or null when the current one was discarded.</summary>
    public event Action<RegistryPullCredential?>? Changed;

    /// <summary>The valid credential, or null while none is available (never an expired one).</summary>
    public RegistryPullCredential? Current
    {
        get
        {
            lock (_gate)
            {
                return _current is { } c && time.GetUtcNow() < c.ExpiresAt ? c : null;
            }
        }
    }

    public async Task RunAsync(Func<PlatformSession?> sessionSource, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var wait = await StepAsync(sessionSource, cancellationToken);
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
    public async Task<TimeSpan> StepAsync(Func<PlatformSession?> sessionSource, CancellationToken cancellationToken)
    {
        await _step.WaitAsync(cancellationToken);
        try
        {
            var session = sessionSource();
            if (session is null)
            {
                Discard("inactive");
                return IdlePoll;
            }

            var now = time.GetUtcNow();
            if (_current is { } current)
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

            return await RequestAsync(session, cancellationToken);
        }
        finally
        {
            _step.Release();
        }
    }

    public void Discard(string reason)
    {
        bool had;
        lock (_gate)
        {
            had = _current is not null;
            _current = null;
        }

        if (!had)
        {
            return;
        }

        _logger.LogInformation("Registry credential discarded: {Reason}.", reason);
        Changed?.Invoke(null);
    }

    public void Dispose()
    {
        Discard("exit");
        _step.Dispose();
    }

    private async Task<TimeSpan> RequestAsync(PlatformSession session, CancellationToken cancellationToken)
    {
        var result = await platform.RequestRegistryCredentialsAsync(session, cancellationToken);
        var now = time.GetUtcNow();
        if (result is { Kind: WorkResultKind.Success, Value: { } response } && response.ExpiresAt > now
            && (response.Kind == "anonymous" || (response.Kind == "bearer" && !string.IsNullOrEmpty(response.Token))))
        {
            var renewAt = now + (response.ExpiresAt - now) * 4 / 5;
            var credential = new RegistryPullCredential(
                response.Registry, response.Repositories, response.Kind, response.Token, response.ExpiresAt, renewAt);
            lock (_gate)
            {
                _current = credential;
            }

            _logger.LogInformation(
                "Registry credential issued for {RepositoryCount} repositories; expires at {ExpiresAt:O}.",
                response.Repositories.Count, response.ExpiresAt);
            Changed?.Invoke(credential);
            return renewAt - now;
        }

        if (result.Kind is WorkResultKind.Inactive or WorkResultKind.Refused or WorkResultKind.Conflict or WorkResultKind.Success)
        {
            Discard("refused");
            return IdlePoll;
        }

        _logger.LogWarning("Registry credential request did not succeed: {Kind}.", result.Kind);
        return RetryDelay;
    }
}
