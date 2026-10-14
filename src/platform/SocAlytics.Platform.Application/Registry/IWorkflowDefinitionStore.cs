using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.Registry;

public sealed record StoredWorkflowDefinition(
    string WorkflowId,
    string WorkflowVersion,
    string DefinitionDigest,
    string DefinitionJson,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy);

public sealed record StoredDefaultWorkflow(
    string WorkflowId,
    string WorkflowVersion,
    DateTimeOffset ChangedAt,
    Guid? ChangedBy,
    long Version);

public interface IWorkflowDefinitionStore
{
    /// <summary>Returns false when the workflow ID and version or the definition digest already exists.</summary>
    Task<bool> TryInsertAsync(StoredWorkflowDefinition definition, CancellationToken cancellationToken);

    Task<StoredWorkflowDefinition?> FindAsync(string workflowId, string workflowVersion, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredWorkflowDefinition>> ListAsync(CancellationToken cancellationToken);

    Task<StoredDefaultWorkflow?> GetDefaultAsync(CancellationToken cancellationToken);

    /// <summary>Sets the default workflow; <paramref name="expectedVersion"/> is null when no default exists yet.</summary>
    Task<VersionedWriteResult> SetDefaultAsync(
        string workflowId, string workflowVersion, Guid? changedBy, DateTimeOffset changedAt, long? expectedVersion, CancellationToken cancellationToken);
}
