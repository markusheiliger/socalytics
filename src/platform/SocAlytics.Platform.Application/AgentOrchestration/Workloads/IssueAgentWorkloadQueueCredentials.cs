using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry.QueueCredentials;

namespace SocAlytics.Platform.Application.AgentOrchestration.Workloads;

public sealed record IssueAgentWorkloadQueueCredentialsCommand(string? UserPublicKey);

public sealed record IssuedAgentWorkloadQueueCredentials(
    string UserJwt,
    DateTimeOffset ExpiresAt,
    string QueueUrl,
    string Stream,
    string Consumer,
    string InboxPrefix);

public sealed class IssueAgentWorkloadQueueCredentialsHandler(
    IQueueCredentialIssuer issuer,
    IOptions<AnalystWorkOptions> work,
    TimeProvider time)
{
    public Task<OperationResult<IssuedAgentWorkloadQueueCredentials>> HandleAsync(
        IssueAgentWorkloadQueueCredentialsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Handle(command));
    }

    private OperationResult<IssuedAgentWorkloadQueueCredentials> Handle(IssueAgentWorkloadQueueCredentialsCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.UserPublicKey) || !issuer.IsValidUserPublicKey(command.UserPublicKey))
        {
            return OperationFailure.Validation("invalid-user-key", new FieldViolation("userPublicKey", "invalid"));
        }

        if (!issuer.CanIssue)
        {
            return OperationFailure.Conflict("queue-credentials-unavailable");
        }

        var expiresAt = time.GetUtcNow() + work.Value.QueueCredentialLifetime;
        var inboxPrefix = "_INBOX_" + Guid.CreateVersion7().ToString("N");
        var jwt = issuer.IssueAgentWorkload(command.UserPublicKey, inboxPrefix, expiresAt);
        return new IssuedAgentWorkloadQueueCredentials(
            jwt, expiresAt, work.Value.QueueUrl, issuer.AgentWorkStream, issuer.AgentWorkConsumer, inboxPrefix);
    }
}
