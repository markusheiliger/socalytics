using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Infrastructure.Workers;

namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

/// <summary>Rebuilds the stamp account JWT with the revoked user keys and pushes it as the system account; the JWTs are never logged.</summary>
internal sealed class NatsQueueAccessRevoker(
    IServiceProvider services,
    NatsTrust trust,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<NatsQueueAccessRevoker> logger) : IQueueAccessRevoker
{
    private const string ClaimsUpdateSubject = "$SYS.REQ.CLAIMS.UPDATE";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastIssuedAt;

    public async Task<int> PushPendingAsync(CancellationToken cancellationToken)
    {
        var url = configuration.GetConnectionString("nats");
        if (!trust.IsJwt || string.IsNullOrWhiteSpace(url))
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IQueueCredentialStore>();
            var now = time.GetUtcNow();
            var revoked = await store.ListRevokedUnexpiredAsync(now, cancellationToken).ConfigureAwait(false);
            var pending = revoked.Where(static c => c.RevocationPushedAt is null).Select(static c => c.QueueCredentialId).ToList();
            if (pending.Count == 0)
            {
                return 0;
            }

            // NATS compares revocation and account times with the wall clock that stamped the user JWTs, and only
            // accepts an account JWT that is newer than the stored one.
            var wallClock = DateTimeOffset.UtcNow;
            var issuedAt = Math.Max(wallClock.ToUnixTimeSeconds(), _lastIssuedAt + 1);
            var revocations = revoked.ToDictionary(static c => c.UserPublicKey, _ => wallClock.ToUnixTimeSeconds());
            var accountJwt = trust.BuildStampAccountJwt(revocations, DateTimeOffset.FromUnixTimeSeconds(issuedAt));

            await PushAsync(url, accountJwt, cancellationToken).ConfigureAwait(false);
            _lastIssuedAt = issuedAt;
            return await store.MarkPushedAsync(pending, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryPushPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PushPendingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Queue revocation push failed and stays pending: {Category}", WorkerLoop.Categorize(ex));
            return false;
        }
    }

    private async Task PushAsync(string url, string accountJwt, CancellationToken cancellationToken)
    {
        var (jwt, seed) = trust.CreateSystemUser(DateTimeOffset.UtcNow.AddMinutes(1));
        await using var connection = new NatsConnection(NatsOpts.Default with
        {
            Url = url,
            Name = "socalytics-platform-system",
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AuthOpts = new NatsAuthOpts { Jwt = jwt, Seed = seed },
        });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            var reply = await connection.RequestAsync<string, string>(
                ClaimsUpdateSubject, accountJwt, cancellationToken: timeout.Token).ConfigureAwait(false);
            if (!IsAccepted(reply.Data))
            {
                throw new MessageTransportException(TransportFailureCategories.PublishRejected);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MessageTransportException(TransportFailureCategories.PublishTimeout);
        }
        catch (Exception ex) when (ex is not MessageTransportException and not OperationCanceledException)
        {
            throw new MessageTransportException(TransportFailureCategories.TransportUnavailable, ex);
        }
    }

    private static bool IsAccepted(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.GetInt32() == 200;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
