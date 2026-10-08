using Dapper;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.Club;

internal sealed class MatchScopeSource(IDbSession session) : ITeamScopeSource
{
    public string ResourceKind => "match";

    public async Task<TeamScope?> ResolveAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ScopeRow>(
            new CommandDefinition(
                """
                SELECT t.id AS TeamId, t.season_id AS SeasonId, s.state AS State
                FROM socalytics.match m
                JOIN socalytics.team t ON t.id = m.team_id
                JOIN socalytics.season s ON s.id = t.season_id
                WHERE m.id = @id
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
