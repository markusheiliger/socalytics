using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog;
using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Application.AgentOrchestration.Execution;

public sealed record ReadPinnedAgentArtifactsCommand(Guid AttemptId, string WorkloadClientId, string FencingToken);

public sealed record PinnedAgentVersionDocument(string AgentId, string Version, string Digest, JsonElement Document);

public sealed record PinnedArtifactDocument(string Kind, string Id, string Version, string Digest, JsonElement Document);

public sealed record PinnedAgentArtifacts(PinnedAgentVersionDocument AgentVersion, IReadOnlyList<PinnedArtifactDocument> Artifacts);

/// <summary>Returns the stored documents of the agent version and artifacts an invocation was pinned to, to the workload of its current attempt.</summary>
public sealed class ReadPinnedAgentArtifactsHandler(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IAgentAttemptStore attempts,
    IAgentInvocationPinStore pins,
    IAgentVersionStore agentVersions,
    IAgentCatalogArtifactStore artifacts,
    IAuditTrail audit,
    IInvocationScopeEvaluator scopes,
    TimeProvider time)
{
    public async Task<OperationResult<PinnedAgentArtifacts>> HandleAsync(
        ReadPinnedAgentArtifactsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The invocation is locked before the attempt, in the same order as the claim.
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var unlocked = await attempts.FindAsync(command.AttemptId, cancellationToken);
        if (unlocked is null || unlocked.WorkloadClientId != command.WorkloadClientId)
        {
            return OperationFailure.NotFound();
        }

        var invocation = await invocations.LockAsync(unlocked.InvocationId, cancellationToken);
        var attempt = await attempts.LockAsync(command.AttemptId, cancellationToken);
        if (invocation is null || attempt is null)
        {
            return OperationFailure.NotFound();
        }

        var now = time.GetUtcNow();
        if (AgentAttemptGuard.StaleDisposition(invocation, attempt, command.FencingToken, now) is { } disposition)
        {
            return AgentExecutionSupport.Stale(disposition);
        }

        if (await AgentScopeLoss.EnforceAsync(scopes, invocations, attempts, audit, invocation, attempt, now, cancellationToken))
        {
            await scope.CommitAsync(cancellationToken);
            return AgentExecutionSupport.Stale(AgentStaleDisposition.ScopeLost);
        }

        if (invocation.AgentVersion is null)
        {
            return AgentExecutionSupport.Stale(AgentStaleDisposition.AttemptNotCurrent);
        }

        var version = await agentVersions.FindAsync(invocation.AgentId, invocation.AgentVersion, cancellationToken)
            ?? throw new InvalidOperationException("The pinned agent version is missing.");
        var documents = new List<PinnedArtifactDocument>();
        foreach (var pin in await pins.ListAsync(invocation.Id, cancellationToken))
        {
            var artifact = await artifacts.FindAsync(pin.Kind, pin.ArtifactId, pin.ArtifactVersion, cancellationToken)
                ?? throw new InvalidOperationException($"The pinned artifact '{pin.ArtifactId}' {pin.ArtifactVersion} is missing.");
            documents.Add(new PinnedArtifactDocument(
                AgentCatalogRecordKinds.ToName(pin.Kind), pin.ArtifactId, pin.ArtifactVersion, artifact.Digest, Parse(artifact.Document)));
        }

        await scope.CommitAsync(cancellationToken);
        return new PinnedAgentArtifacts(
            new PinnedAgentVersionDocument(version.AgentId, version.AgentVersion, version.Digest, Parse(version.Document)), documents);
    }

    private static JsonElement Parse(string document)
    {
        using var parsed = JsonDocument.Parse(document);
        return parsed.RootElement.Clone();
    }
}
