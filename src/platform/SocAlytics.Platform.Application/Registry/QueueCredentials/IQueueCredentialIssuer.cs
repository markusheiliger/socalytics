namespace SocAlytics.Platform.Application.Registry.QueueCredentials;

public interface IQueueCredentialIssuer
{
    string JobsStream { get; }

    bool IsValidUserPublicKey(string userPublicKey);

    string ConsumerName(string capabilityId);

    /// <summary>Signs a user JWT for the public key, limited to pulling from the given consumers; the platform never sees a seed.</summary>
    string Issue(string userPublicKey, string inboxPrefix, DateTimeOffset expiresAt, IReadOnlyCollection<string> consumerNames);

    string AgentWorkStream { get; }

    string AgentWorkSubject { get; }

    string AgentWorkConsumer { get; }

    /// <summary>Signs a user JWT for the public key, limited to pulling and acknowledging from the agent workload consumer.</summary>
    string IssueAgentWorkload(string userPublicKey, string inboxPrefix, DateTimeOffset expiresAt);
}
