using NATS.NKeys;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Queue;

/// <summary>
/// One issued queue credential. The ephemeral user NKey and the JWT exist in memory only and are never
/// persisted or logged; <see cref="ToString"/> redacts them.
/// </summary>
public sealed class QueueCredential : IDisposable
{
    public static readonly TimeSpan DefaultUnfitRedeliveryDelay = TimeSpan.FromSeconds(30);

    private readonly KeyPair _key;

    internal QueueCredential(KeyPair key, QueueCredentialsResponse response, DateTimeOffset issuedAt, DateTimeOffset renewAt)
    {
        _key = key;
        UserJwt = response.UserJwt;
        ExpiresAt = response.ExpiresAt;
        QueueUrl = response.QueueUrl;
        Stream = response.Stream;
        Consumers = response.Consumers;
        InboxPrefix = response.InboxPrefix;
        MaxConcurrentAnalysts = response.MaxConcurrentAnalysts;
        UnfitRedeliveryDelay = response.UnfitRedeliveryDelaySeconds is > 0 and var seconds
            ? TimeSpan.FromSeconds(seconds)
            : DefaultUnfitRedeliveryDelay;
        IssuedAt = issuedAt;
        RenewAt = renewAt;
    }

    public DateTimeOffset IssuedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Two thirds of the lifetime, measured on the Manager's clock.</summary>
    public DateTimeOffset RenewAt { get; }

    public string QueueUrl { get; }

    public string Stream { get; }

    public IReadOnlyList<QueueConsumer> Consumers { get; }

    public string InboxPrefix { get; }

    public int MaxConcurrentAnalysts { get; }

    /// <summary>How long the queue withholds a delivery the host cannot fit; the platform default when the credentials carry none.</summary>
    public TimeSpan UnfitRedeliveryDelay { get; }

    internal string UserJwt { get; }

    internal string Seed => _key.GetSeed();

    public void Dispose() => _key.Dispose();

    public override string ToString() => "QueueCredential(redacted)";
}
