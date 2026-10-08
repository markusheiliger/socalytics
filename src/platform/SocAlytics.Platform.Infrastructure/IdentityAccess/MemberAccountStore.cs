using Dapper;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.IdentityAccess;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess;

internal sealed class MemberAccountStore(IDbSession session) : IMemberAccountStore
{
    public async Task<MemberAccessSnapshot?> GetAccessSnapshotAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);

        var account = await connection.QuerySingleOrDefaultAsync<AccountRow>(
            new CommandDefinition(
                "SELECT membership_status AS Status, password_change_required AS PasswordChangeRequired FROM socalytics.member_account WHERE id = @accountId",
                new { accountId },
                session.Transaction,
                cancellationToken: cancellationToken));
        if (account is null || !MembershipStatusRules.TryParse(account.Status, out var status))
        {
            return null;
        }

        var clubRoles = new HashSet<ClubRole>();
        var clubRows = await connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT role FROM socalytics.club_role_assignment WHERE member_account_id = @accountId",
                new { accountId },
                session.Transaction,
                cancellationToken: cancellationToken));
        foreach (var row in clubRows)
        {
            if (ClubRoleRules.TryParse(row, out var role))
            {
                clubRoles.Add(role);
            }
        }

        var teamRoles = new Dictionary<Guid, TeamRole>();
        var teamRows = await connection.QueryAsync<TeamRoleRow>(
            new CommandDefinition(
                "SELECT team_id AS TeamId, role AS Role FROM socalytics.team_role_assignment WHERE member_account_id = @accountId",
                new { accountId },
                session.Transaction,
                cancellationToken: cancellationToken));
        foreach (var row in teamRows)
        {
            if (TeamRoleRules.TryParse(row.Role, out var role))
            {
                teamRoles[row.TeamId] = role;
            }
        }

        return new MemberAccessSnapshot(status, account.PasswordChangeRequired, clubRoles, teamRoles);
    }

    public async Task<IReadOnlyList<Guid>> ListAllTeamIdsAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<Guid>(
            new CommandDefinition(
                "SELECT id FROM socalytics.team",
                transaction: session.Transaction,
                cancellationToken: cancellationToken));
        return ids.ToList();
    }

    private sealed record AccountRow(string Status, bool PasswordChangeRequired);

    private sealed record TeamRoleRow(Guid TeamId, string Role);
}
