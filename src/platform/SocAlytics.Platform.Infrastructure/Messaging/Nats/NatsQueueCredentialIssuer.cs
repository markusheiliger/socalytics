using Microsoft.Extensions.Options;
using NATS.NKeys;
using SocAlytics.Platform.Application.Registry.QueueCredentials;

namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

/// <summary>Signs Manager user JWTs with the stamp account key; the JWT is never logged.</summary>
internal sealed class NatsQueueCredentialIssuer(NatsTrust trust, IOptions<NatsOptions> options) : IQueueCredentialIssuer
{
    public bool CanIssue => trust.IsJwt;

    public string JobsStream => options.Value.JobsStream;

    public bool IsValidUserPublicKey(string userPublicKey)
    {
        try
        {
            return KeyPair.FromPublicKey(userPublicKey).Prefix == PrefixByte.User;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string ConsumerName(string capabilityId) => NatsTopology.ConsumerPrefix + capabilityId;

    public string Issue(string userPublicKey, string inboxPrefix, DateTimeOffset expiresAt, IReadOnlyCollection<string> consumerNames)
    {
        if (!trust.IsJwt)
        {
            throw new InvalidOperationException("Queue credentials require Nats:Authentication:Mode Jwt.");
        }

        // NATS wildcards match whole tokens only, so the pull and info subjects name each issued consumer.
        var stream = JobsStream;
        var publish = new List<string> { $"$JS.ACK.{stream}.>" };
        foreach (var consumer in consumerNames.Where(static c => c.StartsWith(NatsTopology.ConsumerPrefix, StringComparison.Ordinal)))
        {
            publish.Add($"$JS.API.CONSUMER.MSG.NEXT.{stream}.{consumer}");
            publish.Add($"$JS.API.CONSUMER.INFO.{stream}.{consumer}");
        }

        return trust.CreateUserJwt(userPublicKey, "analyst-manager", publish, [$"{inboxPrefix}.>"], expiresAt);
    }

    public string AgentWorkStream => options.Value.AgentWorkStream;

    public string AgentWorkSubject => options.Value.AgentWorkSubject;

    public string AgentWorkConsumer => options.Value.AgentWorkConsumer;

    public string IssueAgentWorkload(string userPublicKey, string inboxPrefix, DateTimeOffset expiresAt)
    {
        if (!trust.IsJwt)
        {
            throw new InvalidOperationException("Queue credentials require Nats:Authentication:Mode Jwt.");
        }

        var stream = AgentWorkStream;
        var consumer = AgentWorkConsumer;
        string[] publish =
        [
            $"$JS.ACK.{stream}.>",
            $"$JS.API.CONSUMER.MSG.NEXT.{stream}.{consumer}",
            $"$JS.API.CONSUMER.INFO.{stream}.{consumer}",
        ];
        return trust.CreateUserJwt(userPublicKey, "agent-workload", publish, [$"{inboxPrefix}.>"], expiresAt);
    }
}
