using Dapper;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.IdentityAccess;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess;

internal sealed class MemberAccountStore(IDbSession session, TimeProvider time) : IMemberAccountStore
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

    public async Task<MemberProfile?> GetProfileAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var snapshot = await GetAccessSnapshotAsync(accountId, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        var connection = await session.GetConnectionAsync(cancellationToken);
        var name = await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT account_name FROM socalytics.member_account WHERE id = @accountId",
            new { accountId },
            session.Transaction,
            cancellationToken: cancellationToken));
        var rows = await connection.QueryAsync<ProfileTeamRow>(new CommandDefinition(
            "SELECT t.id AS TeamId, t.name AS TeamName, t.season_id AS SeasonId, r.role AS Role " +
            "FROM socalytics.team_role_assignment r JOIN socalytics.team t ON t.id = r.team_id " +
            "WHERE r.member_account_id = @accountId ORDER BY t.name, t.id",
            new { accountId },
            session.Transaction,
            cancellationToken: cancellationToken));
        var teams = new List<MemberTeamRole>();
        foreach (var row in rows)
        {
            if (TeamRoleRules.TryParse(row.Role, out var role))
            {
                teams.Add(new MemberTeamRole(row.TeamId, row.TeamName, row.SeasonId, role));
            }
        }

        return new MemberProfile(accountId, name, snapshot.Status, snapshot.PasswordChangeRequired, snapshot.ClubRoles, teams);
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

    public async Task AssignClubRoleAsync(Guid accountId, ClubRole role, Guid? assignedBy, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.club_role_assignment (member_account_id, role, assigned_at, assigned_by_account_id) " +
            "VALUES (@accountId, @role, @now, @assignedBy) ON CONFLICT (member_account_id, role) DO NOTHING",
            new { accountId, role = role.ToWireValue(), now = time.GetUtcNow(), assignedBy },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<Guid?> FindAccountIdByNameAsync(AccountName name, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM socalytics.member_account WHERE normalized_account_name = @normalized",
            new { normalized = name.Normalized },
            session.Transaction,
            cancellationToken: cancellationToken));
    }

    private sealed record AccountRow(string Status, bool PasswordChangeRequired);

    private sealed record ProfileTeamRow(Guid TeamId, string TeamName, Guid SeasonId, string Role);

    private sealed record TeamRoleRow(Guid TeamId, string Role);
}
