using Dapper;
using Npgsql;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Infrastructure.Persistence.AgentOrchestration.Catalog;

internal static class CatalogRows
{
    public static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    public static DateTimeOffset? Utc(DateTime? value) => value is { } v ? Utc(v) : null;
}

internal sealed class AgentCatalogArtifactStore(IDbSession session) : IAgentCatalogArtifactStore
{
    private const string Select =
        """
        SELECT kind AS Kind, artifact_id AS Id, artifact_version AS Version, version_major AS VersionMajor, digest AS Digest,
               agent_group AS "Group", document::text AS Document, registered_by AS RegisteredBy, registered_at AS RegisteredAt
        FROM socalytics.agent_catalog_artifacts
        """;

    private sealed class ArtifactRow
    {
        public string Kind { get; set; } = "";
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public int VersionMajor { get; set; }
        public string Digest { get; set; } = "";
        public string? Group { get; set; }
        public string Document { get; set; } = "";
        public string RegisteredBy { get; set; } = "";
        public DateTime RegisteredAt { get; set; }
    }

    private static AgentCatalogArtifactRecord ToRecord(ArtifactRow r)
    {
        if (!AgentCatalogRecordKinds.TryParse(r.Kind, out var kind))
        {
            throw new InvalidOperationException($"The persisted artifact kind '{r.Kind}' is unknown.");
        }

        return new AgentCatalogArtifactRecord(
            kind, r.Id, r.Version, r.VersionMajor, r.Digest, r.Group, r.Document, r.RegisteredBy, CatalogRows.Utc(r.RegisteredAt));
    }

    public async Task<CatalogInsertResult<AgentCatalogArtifactRecord>> InsertAsync(
        AgentCatalogArtifactRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var kind = AgentCatalogRecordKinds.ToName(record.Kind);

        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.agent_catalog_artifacts
                (kind, artifact_id, artifact_version, version_major, digest, agent_group, document, registered_by, registered_at)
            VALUES (@kind, @id, @version, @versionMajor, @digest, @group, @document::jsonb, @registeredBy, @registeredAt)
            ON CONFLICT (kind, artifact_id, artifact_version) DO NOTHING
            """,
            new
            {
                kind,
                id = record.Id,
                version = record.Version,
                versionMajor = record.VersionMajor,
                digest = record.Digest,
                group = record.Group,
                document = record.Document,
                registeredBy = record.RegisteredBy,
                registeredAt = record.RegisteredAt,
            },
            transaction,
            cancellationToken: cancellationToken));

        var row = await connection.QuerySingleAsync<ArtifactRow>(new CommandDefinition(
            Select + " WHERE kind = @kind AND artifact_id = @id AND artifact_version = @version",
            new { kind, id = record.Id, version = record.Version }, transaction, cancellationToken: cancellationToken));
        return new CatalogInsertResult<AgentCatalogArtifactRecord>(ToRecord(row), inserted > 0);
    }

    public async Task<AgentCatalogArtifactRecord?> FindAsync(
        AgentCatalogRecordKind kind, string id, string version, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ArtifactRow>(new CommandDefinition(
            Select + " WHERE kind = @kind AND artifact_id = @id AND artifact_version = @version",
            new { kind = AgentCatalogRecordKinds.ToName(kind), id, version }, session.Transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<AgentCatalogArtifactRecord?> FindAnyKindAsync(string id, string version, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<ArtifactRow>(new CommandDefinition(
            Select + " WHERE artifact_id = @id AND artifact_version = @version ORDER BY kind",
            new { id, version }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<IReadOnlyList<AgentCatalogArtifactRecord>> ListAsync(
        AgentCatalogRecordKind kind, string? id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ArtifactRow>(new CommandDefinition(
            Select + " WHERE kind = @kind AND (@id::text IS NULL OR artifact_id = @id) ORDER BY artifact_id, registered_at, artifact_version",
            new { kind = AgentCatalogRecordKinds.ToName(kind), id }, session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<AgentCatalogArtifactRecord>> ListByMajorAsync(
        AgentCatalogRecordKind kind, string id, int major, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ArtifactRow>(new CommandDefinition(
            Select + " WHERE kind = @kind AND artifact_id = @id AND version_major = @major ORDER BY registered_at, artifact_version",
            new { kind = AgentCatalogRecordKinds.ToName(kind), id, major }, session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(ToRecord).ToList();
    }

    public async Task<bool> AnyAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM socalytics.agent_catalog_artifacts)",
            transaction: session.Transaction, cancellationToken: cancellationToken));
    }
}

internal sealed class AgentVersionStore(IDbSession session) : IAgentVersionStore
{
    private const string Select =
        """
        SELECT agent_id AS AgentId, agent_version AS AgentVersion, agent_group AS "Group", digest AS Digest,
               document::text AS Document, state AS State, registered_by AS RegisteredBy, registered_at AS RegisteredAt,
               approved_by AS ApprovedBy, approved_at AS ApprovedAt, self_approval AS SelfApproval,
               differentiation_acknowledged AS DifferentiationAcknowledged, retired_by AS RetiredBy, retired_at AS RetiredAt,
               version AS Version
        FROM socalytics.agent_versions
        """;

    private sealed class VersionRow
    {
        public string AgentId { get; set; } = "";
        public string AgentVersion { get; set; } = "";
        public string Group { get; set; } = "";
        public string Digest { get; set; } = "";
        public string Document { get; set; } = "";
        public string State { get; set; } = "";
        public string RegisteredBy { get; set; } = "";
        public DateTime RegisteredAt { get; set; }
        public string? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public bool? SelfApproval { get; set; }
        public bool? DifferentiationAcknowledged { get; set; }
        public string? RetiredBy { get; set; }
        public DateTime? RetiredAt { get; set; }
        public long Version { get; set; }
    }

    private static AgentVersionRecord ToRecord(VersionRow r)
    {
        if (!AgentVersionStates.TryParse(r.State, out var state))
        {
            throw new InvalidOperationException($"The persisted agent version state '{r.State}' is unknown.");
        }

        return new AgentVersionRecord(
            r.AgentId, r.AgentVersion, r.Group, r.Digest, r.Document, state, r.RegisteredBy, CatalogRows.Utc(r.RegisteredAt),
            r.ApprovedBy, CatalogRows.Utc(r.ApprovedAt), r.SelfApproval, r.DifferentiationAcknowledged, r.RetiredBy,
            CatalogRows.Utc(r.RetiredAt), r.Version);
    }

    public async Task<CatalogInsertResult<AgentVersionRecord>> InsertAsync(AgentVersionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);

        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.agent_versions
                (agent_id, agent_version, agent_group, digest, document, state, registered_by, registered_at)
            VALUES (@agentId, @agentVersion, @group, @digest, @document::jsonb, 'draft', @registeredBy, @registeredAt)
            ON CONFLICT (agent_id, agent_version) DO NOTHING
            """,
            new
            {
                agentId = record.AgentId,
                agentVersion = record.AgentVersion,
                group = record.Group,
                digest = record.Digest,
                document = record.Document,
                registeredBy = record.RegisteredBy,
                registeredAt = record.RegisteredAt,
            },
            transaction,
            cancellationToken: cancellationToken));

        var row = await connection.QuerySingleAsync<VersionRow>(new CommandDefinition(
            Select + " WHERE agent_id = @agentId AND agent_version = @agentVersion",
            new { agentId = record.AgentId, agentVersion = record.AgentVersion }, transaction, cancellationToken: cancellationToken));
        return new CatalogInsertResult<AgentVersionRecord>(ToRecord(row), inserted > 0);
    }

    public async Task<AgentVersionRecord?> FindAsync(string agentId, string agentVersion, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<VersionRow>(new CommandDefinition(
            Select + " WHERE agent_id = @agentId AND agent_version = @agentVersion",
            new { agentId, agentVersion }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public async Task<IReadOnlyList<AgentVersionRecord>> ListAsync(
        string? agentId, AgentVersionState? state, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<VersionRow>(new CommandDefinition(
            Select +
            " WHERE (@agentId::text IS NULL OR agent_id = @agentId) AND (@state::text IS NULL OR state = @state)" +
            " ORDER BY agent_id, registered_at, agent_version",
            new { agentId, state = state is { } s ? AgentVersionStates.ToPersistedName(s) : null },
            session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(ToRecord).ToList();
    }

    public Task<VersionedWriteResult> ApproveAsync(
        string agentId, string agentVersion, AgentVersionApproval approval, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var transaction = session.RequireTransaction();
        return VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(
                """
                UPDATE socalytics.agent_versions
                SET state = 'approved', approved_by = @approvedBy, approved_at = @approvedAt,
                    self_approval = @selfApproval, differentiation_acknowledged = @differentiationAcknowledged
                WHERE agent_id = @agentId AND agent_version = @agentVersion AND state = 'draft' AND version = @expectedVersion
                RETURNING version
                """,
                new
                {
                    agentId,
                    agentVersion,
                    approvedBy = approval.ApprovedBy,
                    approvedAt = approval.ApprovedAt,
                    selfApproval = approval.SelfApproval,
                    differentiationAcknowledged = approval.DifferentiationAcknowledged,
                    expectedVersion,
                },
                transaction,
                cancellationToken: cancellationToken),
            Probe(agentId, agentVersion, transaction, cancellationToken));
    }

    public Task<VersionedWriteResult> RetireAsync(
        string agentId, string agentVersion, AgentVersionRetirement retirement, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retirement);
        var transaction = session.RequireTransaction();
        return VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(
                """
                UPDATE socalytics.agent_versions
                SET state = 'retired', retired_by = @retiredBy, retired_at = @retiredAt
                WHERE agent_id = @agentId AND agent_version = @agentVersion AND state = 'approved' AND version = @expectedVersion
                RETURNING version
                """,
                new { agentId, agentVersion, retiredBy = retirement.RetiredBy, retiredAt = retirement.RetiredAt, expectedVersion },
                transaction,
                cancellationToken: cancellationToken),
            Probe(agentId, agentVersion, transaction, cancellationToken));
    }

    private static CommandDefinition Probe(
        string agentId, string agentVersion, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        new(
            "SELECT version FROM socalytics.agent_versions WHERE agent_id = @agentId AND agent_version = @agentVersion",
            new { agentId, agentVersion }, transaction, cancellationToken: cancellationToken);
}

internal sealed class AgentEnablementStore(IDbSession session) : IAgentEnablementStore
{
    private const string Select =
        """
        SELECT agent_id AS AgentId, agent_version AS AgentVersion, changed_by AS ChangedBy, changed_at AS ChangedAt,
               version AS Version
        FROM socalytics.agent_enablements
        """;

    private sealed class EnablementRow
    {
        public string AgentId { get; set; } = "";
        public string? AgentVersion { get; set; }
        public string? ChangedBy { get; set; }
        public DateTime? ChangedAt { get; set; }
        public long Version { get; set; }
    }

    private static AgentEnablementRecord ToRecord(EnablementRow r) =>
        new(r.AgentId, r.AgentVersion, r.ChangedBy, CatalogRows.Utc(r.ChangedAt), r.Version);

    public async Task<IReadOnlyList<AgentEnablementRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<EnablementRow>(new CommandDefinition(
            Select + " ORDER BY agent_id", transaction: session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(ToRecord).ToList();
    }

    public async Task<AgentEnablementRecord?> FindAsync(string agentId, bool forShare, CancellationToken cancellationToken)
    {
        if (forShare)
        {
            session.RequireTransaction();
        }

        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<EnablementRow>(new CommandDefinition(
            Select + " WHERE agent_id = @agentId" + (forShare ? " FOR SHARE" : string.Empty),
            new { agentId }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToRecord(row);
    }

    public Task<VersionedWriteResult> SetAsync(
        string agentId, string? agentVersion, string changedBy, DateTimeOffset changedAt, long expectedVersion,
        CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        return VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(
                """
                UPDATE socalytics.agent_enablements
                SET agent_version = @agentVersion, changed_by = @changedBy, changed_at = @changedAt
                WHERE agent_id = @agentId AND version = @expectedVersion
                RETURNING version
                """,
                new { agentId, agentVersion, changedBy, changedAt, expectedVersion },
                transaction,
                cancellationToken: cancellationToken),
            new CommandDefinition(
                "SELECT version FROM socalytics.agent_enablements WHERE agent_id = @agentId",
                new { agentId }, transaction, cancellationToken: cancellationToken));
    }
}

internal sealed class AgentInvocationPinStore(IDbSession session) : IAgentInvocationPinStore
{
    private sealed class PinRow
    {
        public string Kind { get; set; } = "";
        public string ArtifactId { get; set; } = "";
        public string ArtifactVersion { get; set; } = "";
        public string Digest { get; set; } = "";
    }

    public async Task InsertAsync(Guid invocationId, IReadOnlyList<InvocationPin> pins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pins);
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO socalytics.agent_invocation_pins (invocation_id, kind, artifact_id, artifact_version, digest)
            VALUES (@invocationId, @kind, @artifactId, @artifactVersion, @digest)
            """,
            pins.Select(pin => new
            {
                invocationId,
                kind = AgentCatalogRecordKinds.ToName(pin.Kind),
                artifactId = pin.ArtifactId,
                artifactVersion = pin.ArtifactVersion,
                digest = pin.Digest,
            }).ToList(),
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<InvocationPin>> ListAsync(Guid invocationId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PinRow>(new CommandDefinition(
            """
            SELECT kind AS Kind, artifact_id AS ArtifactId, artifact_version AS ArtifactVersion, digest AS Digest
            FROM socalytics.agent_invocation_pins
            WHERE invocation_id = @invocationId
            """,
            new { invocationId }, session.Transaction, cancellationToken: cancellationToken));

        return rows
            .Select(r => AgentCatalogRecordKinds.TryParse(r.Kind, out var kind)
                ? new InvocationPin(kind, r.ArtifactId, r.ArtifactVersion, r.Digest)
                : throw new InvalidOperationException($"The persisted pin kind '{r.Kind}' is unknown."))
            .OrderBy(pin => pin.Kind)
            .ToList();
    }
}
