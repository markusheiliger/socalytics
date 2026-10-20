using Dapper;
using SocAlytics.Platform.Application.AgentOrchestration;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.AgentOrchestration;

internal sealed class AgentConversationScopeSource(IDbSession session) : ITeamScopeSource
{
    public string ResourceKind => AgentOrchestrationAuditEvents.ConversationResourceType;

    public Task<TeamScope?> ResolveAsync(Guid id, CancellationToken cancellationToken) =>
        AgentScopeQuery.ResolveAsync(session, "agent_conversations", id, cancellationToken);
}

internal sealed class AgentInvocationScopeSource(IDbSession session) : ITeamScopeSource
{
    public string ResourceKind => AgentOrchestrationAuditEvents.InvocationResourceType;

    public Task<TeamScope?> ResolveAsync(Guid id, CancellationToken cancellationToken) =>
        AgentScopeQuery.ResolveAsync(session, "agent_invocations", id, cancellationToken);
}

internal static class AgentScopeQuery
{
    // The table name is one of two constants chosen by the callers, never input.
    public static async Task<TeamScope?> ResolveAsync(
        IDbSession session, string table, Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ScopeRow>(
            new CommandDefinition(
                $"""
                SELECT t.id AS TeamId, t.season_id AS SeasonId, s.state AS State
                FROM socalytics.{table} r
                JOIN socalytics.team t ON t.id = r.team_id
                JOIN socalytics.season s ON s.id = t.season_id
                WHERE r.id = @id
                """,
                new { id },
                session.Transaction,
                cancellationToken: cancellationToken));

        return row is not null && SeasonStateWire.TryParse(row.State, out var state)
            ? new TeamScope(row.TeamId, row.SeasonId, state)
            : null;
    }

    private sealed record ScopeRow(Guid TeamId, Guid SeasonId, string State);
}
