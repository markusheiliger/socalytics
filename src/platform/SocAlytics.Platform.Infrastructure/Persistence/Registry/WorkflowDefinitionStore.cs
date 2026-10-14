using Dapper;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry;

namespace SocAlytics.Platform.Infrastructure.Persistence.Registry;

internal sealed class WorkflowDefinitionStore(IDbSession session) : IWorkflowDefinitionStore
{
    private const string Select =
        "SELECT workflow_id AS WorkflowId, workflow_version AS WorkflowVersion, definition_digest AS DefinitionDigest, " +
        "definition::text AS DefinitionJson, created_at AS CreatedAt, created_by AS CreatedBy FROM socalytics.workflow_definition";

    private sealed class Row
    {
        public string WorkflowId { get; set; } = "";
        public string WorkflowVersion { get; set; } = "";
        public string DefinitionDigest { get; set; } = "";
        public string DefinitionJson { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public Guid? CreatedBy { get; set; }
    }

    private sealed class DefaultRow
    {
        public string WorkflowId { get; set; } = "";
        public string WorkflowVersion { get; set; } = "";
        public DateTime ChangedAt { get; set; }
        public Guid? ChangedBy { get; set; }
        public long Version { get; set; }
    }

    public async Task<bool> TryInsertAsync(StoredWorkflowDefinition d, CancellationToken cancellationToken)
    {
        session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.workflow_definition (workflow_id, workflow_version, definition_digest, definition, created_at, created_by) " +
            "VALUES (@WorkflowId, @WorkflowVersion, @DefinitionDigest, @DefinitionJson::jsonb, @CreatedAt, @CreatedBy) ON CONFLICT DO NOTHING",
            d, session.Transaction, cancellationToken: cancellationToken));
        return rows == 1;
    }

    public async Task<StoredWorkflowDefinition?> FindAsync(string workflowId, string workflowVersion, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Select + " WHERE workflow_id = @workflowId AND workflow_version = @workflowVersion",
            new { workflowId, workflowVersion }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<StoredWorkflowDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            Select + " ORDER BY workflow_id, workflow_version", transaction: session.Transaction, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public async Task<StoredDefaultWorkflow?> GetDefaultAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<DefaultRow>(new CommandDefinition(
            "SELECT workflow_id AS WorkflowId, workflow_version AS WorkflowVersion, changed_at AS ChangedAt, " +
            "changed_by AS ChangedBy, version AS Version FROM socalytics.default_workflow",
            transaction: session.Transaction, cancellationToken: cancellationToken));
        return row is null
            ? null
            : new StoredDefaultWorkflow(row.WorkflowId, row.WorkflowVersion, Utc(row.ChangedAt), row.ChangedBy, row.Version);
    }

    public async Task<VersionedWriteResult> SetDefaultAsync(
        string workflowId, string workflowVersion, Guid? changedBy, DateTimeOffset changedAt, long? expectedVersion, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var args = new { workflowId, workflowVersion, changedBy, changedAt, expectedVersion };

        if (expectedVersion is null)
        {
            var inserted = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                "INSERT INTO socalytics.default_workflow (singleton, workflow_id, workflow_version, changed_at, changed_by) " +
                "VALUES (true, @workflowId, @workflowVersion, @changedAt, @changedBy) ON CONFLICT DO NOTHING RETURNING version",
                args, transaction, cancellationToken: cancellationToken));
            if (inserted is { } version)
            {
                return new VersionedWriteResult(VersionedWriteOutcome.Applied, version);
            }

            var existing = await connection.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT version FROM socalytics.default_workflow", transaction: transaction, cancellationToken: cancellationToken));
            return new VersionedWriteResult(VersionedWriteOutcome.ConcurrencyConflict, existing);
        }

        return await VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(
                "UPDATE socalytics.default_workflow SET workflow_id = @workflowId, workflow_version = @workflowVersion, " +
                "changed_at = @changedAt, changed_by = @changedBy WHERE singleton AND version = @expectedVersion RETURNING version",
                args, transaction, cancellationToken: cancellationToken),
            new CommandDefinition(
                "SELECT version FROM socalytics.default_workflow WHERE singleton",
                transaction: transaction, cancellationToken: cancellationToken));
    }

    private static StoredWorkflowDefinition Map(Row r) =>
        new(r.WorkflowId, r.WorkflowVersion, r.DefinitionDigest, r.DefinitionJson, Utc(r.CreatedAt), r.CreatedBy);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
