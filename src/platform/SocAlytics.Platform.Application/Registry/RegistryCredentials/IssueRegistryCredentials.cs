using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.RegistryCredentials;

public sealed record IssuedRegistryCredentials(
    string Registry,
    IReadOnlyList<string> Repositories,
    string Kind,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Token,
    DateTimeOffset ExpiresAt);

public sealed class IssueRegistryCredentialsHandler(
    IUnitOfWork unitOfWork,
    IRegistrationStore registrations,
    ICapabilityDeclarationStore declarations,
    IWorkflowDefinitionResolver workflows,
    IRegistryPullTokenIssuer issuer)
{
    public async Task<OperationResult<IssuedRegistryCredentials>> HandleAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var registration = await registrations.FindByIdAsync(registrationId, cancellationToken);
        if (registration is null)
        {
            return OperationFailure.Unauthenticated();
        }

        if (registration.State != RegistrationState.Active)
        {
            return OperationFailure.Forbidden("manager-not-active");
        }

        var resolution = await workflows.ResolveDefaultAsync(cancellationToken);
        if (resolution is not WorkflowResolution.ResolvedResult resolved)
        {
            return WorkflowFailure(resolution);
        }

        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var node in resolved.Definition.Nodes)
        {
            var declaration = await declarations.FindAsync(ToOciDigest(node.ImageDigest.ToString()), cancellationToken);
            if (declaration is not null && IsInsidePrefix(declaration.Repository))
            {
                found.Add(declaration.Repository);
            }
        }

        if (found.Count == 0)
        {
            return OperationFailure.Conflict("workflow-not-configured");
        }

        var repositories = found.ToList();
        try
        {
            var token = await issuer.IssueAsync(repositories, cancellationToken);
            return new IssuedRegistryCredentials(issuer.RegistryHost, repositories, token.Kind, token.Token, token.ExpiresAt);
        }
        catch (RegistryPullTokenException)
        {
            return OperationFailure.Unavailable("registry-unavailable");
        }
    }

    internal static OperationFailure WorkflowFailure(WorkflowResolution resolution) =>
        resolution is WorkflowResolution.UnavailableResult { Category: WorkflowResolution.RegistryUnavailable }
            ? OperationFailure.Unavailable("registry-unavailable")
            : OperationFailure.Conflict("workflow-not-configured");

    /// <summary>The platform digest form (<c>sha-256:</c>) back to the OCI form the Registry stores.</summary>
    internal static string ToOciDigest(string digest) =>
        digest.StartsWith(Domain.Recordings.Sha256Digest.Prefix, StringComparison.Ordinal)
            ? "sha256:" + digest[Domain.Recordings.Sha256Digest.Prefix.Length..]
            : digest;

    private bool IsInsidePrefix(string repository) =>
        string.IsNullOrEmpty(issuer.RepositoryPrefix) || repository.StartsWith(issuer.RepositoryPrefix, StringComparison.Ordinal);
}
