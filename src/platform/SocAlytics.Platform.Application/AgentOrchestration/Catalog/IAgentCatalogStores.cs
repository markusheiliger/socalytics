using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Application.AgentOrchestration.Catalog;

public sealed record AgentCatalogArtifactRecord(
    AgentCatalogRecordKind Kind,
    string Id,
    string Version,
    int VersionMajor,
    string Digest,
    string? Group,
    string Document,
    string RegisteredBy,
    DateTimeOffset RegisteredAt);

// Created is false when the identity already existed and Record is the stored row.
public sealed record CatalogInsertResult<T>(T Record, bool Created)
    where T : class;

public interface IAgentCatalogArtifactStore
{
    /// <summary>Inserts once; on a (kind, id, version) conflict the existing row is returned and nothing changes.</summary>
    Task<CatalogInsertResult<AgentCatalogArtifactRecord>> InsertAsync(AgentCatalogArtifactRecord record, CancellationToken cancellationToken);

    Task<AgentCatalogArtifactRecord?> FindAsync(AgentCatalogRecordKind kind, string id, string version, CancellationToken cancellationToken);

    /// <summary>Finds an artifact by identity under any kind, to tell a missing reference from one of another kind.</summary>
    Task<AgentCatalogArtifactRecord?> FindAnyKindAsync(string id, string version, CancellationToken cancellationToken);

    /// <summary>Lists the artifacts of a kind, optionally of one identifier, ordered by identifier and registration time.</summary>
    Task<IReadOnlyList<AgentCatalogArtifactRecord>> ListAsync(AgentCatalogRecordKind kind, string? id, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentCatalogArtifactRecord>> ListByMajorAsync(AgentCatalogRecordKind kind, string id, int major, CancellationToken cancellationToken);

    Task<bool> AnyAsync(CancellationToken cancellationToken);
}

public sealed record AgentVersionRecord(
    string AgentId,
    string AgentVersion,
    string Group,
    string Digest,
    string Document,
    AgentVersionState State,
    string RegisteredBy,
    DateTimeOffset RegisteredAt,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    bool? SelfApproval,
    bool? DifferentiationAcknowledged,
    string? RetiredBy,
    DateTimeOffset? RetiredAt,
    long Version);

public sealed record AgentVersionApproval(string ApprovedBy, DateTimeOffset ApprovedAt, bool SelfApproval, bool DifferentiationAcknowledged);

public sealed record AgentVersionRetirement(string RetiredBy, DateTimeOffset RetiredAt);

public interface IAgentVersionStore
{
    /// <summary>Inserts once; on an (agent id, version) conflict the existing row is returned and nothing changes.</summary>
    Task<CatalogInsertResult<AgentVersionRecord>> InsertAsync(AgentVersionRecord record, CancellationToken cancellationToken);

    Task<AgentVersionRecord?> FindAsync(string agentId, string agentVersion, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentVersionRecord>> ListAsync(string? agentId, AgentVersionState? state, CancellationToken cancellationToken);

    /// <summary>Moves a draft to approved when the row still has <paramref name="expectedVersion"/>.</summary>
    Task<VersionedWriteResult> ApproveAsync(string agentId, string agentVersion, AgentVersionApproval approval, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>Moves an approved version to retired when the row still has <paramref name="expectedVersion"/>.</summary>
    Task<VersionedWriteResult> RetireAsync(string agentId, string agentVersion, AgentVersionRetirement retirement, long expectedVersion, CancellationToken cancellationToken);
}

public sealed record AgentEnablementRecord(
    string AgentId,
    string? AgentVersion,
    string? ChangedBy,
    DateTimeOffset? ChangedAt,
    long Version);

public interface IAgentEnablementStore
{
    Task<IReadOnlyList<AgentEnablementRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Reads one enablement row; <paramref name="forShare"/> takes a share lock on it for the rest of the unit of work.</summary>
    Task<AgentEnablementRecord?> FindAsync(string agentId, bool forShare, CancellationToken cancellationToken);

    /// <summary>Sets the enabled version (null disables) when the row still has <paramref name="expectedVersion"/>.</summary>
    Task<VersionedWriteResult> SetAsync(
        string agentId, string? agentVersion, string changedBy, DateTimeOffset changedAt, long expectedVersion, CancellationToken cancellationToken);
}

public interface IAgentInvocationPinStore
{
    /// <summary>Inserts the pins of a new invocation in the current unit of work; pins are never updated.</summary>
    Task InsertAsync(Guid invocationId, IReadOnlyList<InvocationPin> pins, CancellationToken cancellationToken);

    Task<IReadOnlyList<InvocationPin>> ListAsync(Guid invocationId, CancellationToken cancellationToken);
}
