using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.QueueCredentials;

public sealed record CapabilityReference(string Id, string Version);

public sealed record IssueQueueCredentialsCommand(
    Guid RegistrationId, string? UserPublicKey, IReadOnlyList<CapabilityReference>? Capabilities);

public sealed record QueueConsumerAssignment(string Capability, string Consumer);

public sealed record IssuedQueueCredentials(
    string UserJwt,
    DateTimeOffset ExpiresAt,
    string QueueUrl,
    string Stream,
    IReadOnlyList<QueueConsumerAssignment> Consumers,
    string InboxPrefix,
    int MaxConcurrentAnalysts);

public sealed class IssueQueueCredentialsHandler(
    IUnitOfWork unitOfWork,
    IRegistrationStore registrations,
    IQueueCredentialStore credentials,
    IQueueCredentialIssuer issuer,
    IWorkflowDefinitionResolver workflows,
    IOptions<AnalystWorkOptions> work,
    TimeProvider time)
{
    public async Task<OperationResult<IssuedQueueCredentials>> HandleAsync(
        IssueQueueCredentialsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.UserPublicKey) || !issuer.IsValidUserPublicKey(command.UserPublicKey))
        {
            return OperationFailure.Validation("invalid-user-key", new FieldViolation("userPublicKey", "invalid"));
        }

        if (command.Capabilities is null
            || command.Capabilities.Any(static c => c is null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Version)))
        {
            return OperationFailure.Validation("invalid-capabilities", new FieldViolation("capabilities", "invalid"));
        }

        var resolution = await workflows.ResolveDefaultAsync(cancellationToken);
        var known = resolution is WorkflowResolution.ResolvedResult resolved
            ? resolved.Definition.Nodes.Select(static n => (n.CapabilityId, n.CapabilityVersion)).ToHashSet()
            : [];
        var consumers = command.Capabilities
            .Where(c => known.Contains((c.Id, c.Version)))
            .Select(static c => c.Id)
            .Distinct(StringComparer.Ordinal)
            .Select(id => new QueueConsumerAssignment(id, issuer.ConsumerName(id)))
            .ToList();

        var now = time.GetUtcNow();
        var expiresAt = now + work.Value.QueueCredentialLifetime;
        var inboxPrefix = "_INBOX_" + command.RegistrationId.ToString("N");

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var registration = await registrations.FindByIdAsync(command.RegistrationId, cancellationToken);
        if (registration is null)
        {
            return OperationFailure.Unauthenticated();
        }

        if (registration.State != RegistrationState.Active)
        {
            return OperationFailure.Forbidden("manager-not-active");
        }

        var recorded = await credentials.RecordIssuanceAsync(
            new IssuedQueueCredential(Guid.CreateVersion7(), registration.Id, command.UserPublicKey, now, expiresAt, null, null),
            cancellationToken);
        if (!recorded)
        {
            return OperationFailure.Conflict("user-key-reused");
        }

        var jwt = issuer.Issue(command.UserPublicKey, inboxPrefix, expiresAt, consumers.Select(static c => c.Consumer).ToList());
        await scope.CommitAsync(cancellationToken);
        return new IssuedQueueCredentials(
            jwt, expiresAt, work.Value.QueueUrl, issuer.JobsStream, consumers, inboxPrefix,
            work.Value.MaxConcurrentAnalystsPerManager);
    }
}
