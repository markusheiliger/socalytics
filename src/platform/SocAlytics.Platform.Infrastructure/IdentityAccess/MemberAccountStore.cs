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

    public async Task<MemberDetails?> GetMemberAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MemberRow>(new CommandDefinition(
            "SELECT account_name AS AccountName, membership_status AS Status, (password_hash IS NOT NULL) AS PasswordSet, " +
            "lockout_end AS LockoutEnd, two_factor_enabled AS TwoFactorEnabled, created_at AS CreatedAt, version AS Version " +
            "FROM socalytics.member_account WHERE id = @id",
            new { id },
            session.Transaction,
            cancellationToken: cancellationToken));
        var snapshot = await GetAccessSnapshotAsync(id, cancellationToken);
        if (row is null || snapshot is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var lockoutEnd = row.LockoutEnd is { } le ? new DateTimeOffset(DateTime.SpecifyKind(le, DateTimeKind.Utc)) : (DateTimeOffset?)null;
        var lockedOut = lockoutEnd is { } end && end > now;
        return new MemberDetails(
            id,
            row.AccountName,
            snapshot.Status,
            row.PasswordSet,
            lockedOut,
            lockedOut ? lockoutEnd : null,
            row.TwoFactorEnabled,
            snapshot.ClubRoles,
            [.. snapshot.TeamRoles.OrderBy(t => t.Key).Select(t => new MemberTeamAssignment(t.Key, t.Value))],
            new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
            row.Version);
    }

    public async Task<MemberPageResult> ListMembersAsync(MemberPageKey? after, int pageSize, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var keys = (await connection.QueryAsync<PageKeyRow>(new CommandDefinition(
            "SELECT id AS Id, normalized_account_name AS NormalizedAccountName FROM socalytics.member_account " +
            "WHERE @hasAfter = false OR (normalized_account_name, id) > (@afterName, @afterId) " +
            "ORDER BY normalized_account_name, id LIMIT @limit",
            new
            {
                hasAfter = after is not null,
                afterName = after?.NormalizedAccountName ?? string.Empty,
                afterId = after?.Id ?? Guid.Empty,
                limit = pageSize + 1,
            },
            session.Transaction,
            cancellationToken: cancellationToken))).ToList();

        var hasMore = keys.Count > pageSize;
        var pageKeys = keys.Take(pageSize).ToList();
        var items = new List<MemberDetails>(pageKeys.Count);
        foreach (var key in pageKeys)
        {
            if (await GetMemberAsync(key.Id, cancellationToken) is { } member)
            {
                items.Add(member);
            }
        }

        var last = pageKeys.Count > 0 ? new MemberPageKey(pageKeys[^1].NormalizedAccountName, pageKeys[^1].Id) : null;
        return new MemberPageResult(items, hasMore, last);
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

    public async Task<LockedAccount?> LockAccountAsync(Guid id, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var status = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT membership_status FROM socalytics.member_account WHERE id = @id FOR UPDATE",
            new { id },
            transaction,
            cancellationToken: cancellationToken));
        if (status is null || !MembershipStatusRules.TryParse(status, out var parsed))
        {
            return null;
        }

        var roles = new HashSet<ClubRole>();
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT role FROM socalytics.club_role_assignment WHERE member_account_id = @id",
            new { id },
            transaction,
            cancellationToken: cancellationToken));
        foreach (var row in rows)
        {
            if (ClubRoleRules.TryParse(row, out var role))
            {
                roles.Add(role);
            }
        }

        return new LockedAccount(parsed, roles);
    }

    public async Task LockClubAdminInvariantAsync(CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@key)",
            new { key = AdvisoryLockKeys.ClubAdminInvariant },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<int> CountOtherActiveClubAdminsAsync(Guid exceptAccountId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM socalytics.club_role_assignment r " +
            "JOIN socalytics.member_account a ON a.id = r.member_account_id " +
            "WHERE r.role = 'club-admin' AND a.membership_status = 'active' AND a.id <> @exceptAccountId",
            new { exceptAccountId },
            session.Transaction,
            cancellationToken: cancellationToken));
    }

    public async Task RemoveClubRoleAsync(Guid accountId, ClubRole role, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM socalytics.club_role_assignment WHERE member_account_id = @accountId AND role = @role",
            new { accountId, role = role.ToWireValue() },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task DeactivateAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM socalytics.club_role_assignment WHERE member_account_id = @accountId; " +
            "DELETE FROM socalytics.team_role_assignment WHERE member_account_id = @accountId; " +
            "UPDATE socalytics.member_account SET membership_status = 'deactivated', membership_changed_at = @now, " +
            "security_stamp = @stamp WHERE id = @accountId",
            new { accountId, now = time.GetUtcNow(), stamp = Guid.NewGuid().ToString("N") },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task ReactivateAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE socalytics.member_account SET membership_status = 'active', membership_changed_at = @now WHERE id = @accountId",
            new { accountId, now = time.GetUtcNow() },
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

    private sealed record MemberRow(
        string AccountName,
        string Status,
        bool PasswordSet,
        DateTime? LockoutEnd,
        bool TwoFactorEnabled,
        DateTime CreatedAt,
        long Version);

    private sealed record PageKeyRow(Guid Id, string NormalizedAccountName);

    private sealed record AccountRow(string Status, bool PasswordChangeRequired);

    private sealed record ProfileTeamRow(Guid TeamId, string TeamName, Guid SeasonId, string Role);

    private sealed record TeamRoleRow(Guid TeamId, string Role);
}
