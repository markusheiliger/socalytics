using Dapper;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess.Invitations;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Infrastructure.Persistence.IdentityAccess;

internal sealed class MemberInvitationStore(IDbSession session) : IMemberInvitationStore
{
    // The open link is the newest link that is neither used nor superseded.
    private const string InvitationSelect =
        "SELECT i.id AS Id, i.address AS Address, i.address_normalized AS AddressNormalized, i.address_hash AS AddressHash, " +
        "i.account_name AS AccountName, i.state AS State, i.invited_by AS InvitedBy, i.created_at AS CreatedAt, " +
        "i.resend_count AS ResendCount, i.revoked_by AS RevokedBy, i.revoked_at AS RevokedAt, " +
        "i.accepted_member_id AS AcceptedMemberId, i.accepted_at AS AcceptedAt, ol.issued_at AS OpenLinkIssuedAt, " +
        "ol.expires_at AS OpenLinkExpiresAt, i.version AS Version " +
        "FROM socalytics.member_invitations i " +
        "LEFT JOIN LATERAL (SELECT l.issued_at, l.expires_at FROM socalytics.member_invitation_links l " +
        "WHERE l.invitation_id = i.id AND l.state IN ('pending', 'issued') ORDER BY l.created_at DESC, l.id DESC LIMIT 1) ol ON true ";

    private const string LinkColumns =
        "id AS Id, invitation_id AS InvitationId, state AS State, token_hash AS TokenHash, created_at AS CreatedAt, " +
        "issued_at AS IssuedAt, expires_at AS ExpiresAt, used_at AS UsedAt";

    public async Task<bool> IsAccountNameTakenAsync(string accountNameNormalized, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM socalytics.member_account WHERE normalized_account_name = @accountNameNormalized) " +
            "OR EXISTS (SELECT 1 FROM socalytics.member_invitations WHERE state = 'pending' AND account_name_normalized = @accountNameNormalized)",
            new { accountNameNormalized }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> IsAddressInUseAsync(string addressNormalized, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM socalytics.member_email_addresses WHERE address_normalized = @addressNormalized) " +
            "OR EXISTS (SELECT 1 FROM socalytics.member_invitations WHERE state = 'pending' AND address_normalized = @addressNormalized)",
            new { addressNormalized }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> InsertAsync(NewMemberInvitation invitation, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.member_invitations (id, address, address_normalized, address_hash, account_name, " +
            "account_name_normalized, state, invited_by, created_at) " +
            "VALUES (@Id, @Address, @AddressNormalized, @AddressHash, @AccountName, @AccountNameNormalized, 'pending', @InvitedBy, @CreatedAt) " +
            "ON CONFLICT DO NOTHING",
            new
            {
                invitation.Id,
                invitation.Address,
                invitation.AddressNormalized,
                invitation.AddressHash,
                AccountName = invitation.AccountName.Value,
                AccountNameNormalized = invitation.AccountName.Normalized,
                invitation.InvitedBy,
                invitation.CreatedAt,
            },
            transaction, cancellationToken: cancellationToken));
        if (inserted == 0)
        {
            return false;
        }

        foreach (var role in invitation.TeamRoles)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO socalytics.member_invitation_team_roles (invitation_id, team_id, role) VALUES (@invitationId, @teamId, @role)",
                new { invitationId = invitation.Id, teamId = role.TeamId, role = role.Role.ToWireValue() },
                transaction, cancellationToken: cancellationToken));
        }

        return true;
    }

    public async Task<StoredMemberInvitation?> FindForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        // The row lock is taken on the invitation only; the open link is read inside the same locked statement.
        var row = await connection.QuerySingleOrDefaultAsync<InvitationRow>(new CommandDefinition(
            InvitationSelect + "WHERE i.id = @id FOR UPDATE OF i", new { id }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : await ToStoredAsync(row, cancellationToken);
    }

    public async Task<IReadOnlyList<InvitationListItem>> ListAsync(
        InvitationDisplayState? displayState, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ListRow>(new CommandDefinition(
            "SELECT * FROM (" +
            "SELECT i.id AS Id, i.address AS Address, i.address_normalized AS AddressNormalized, i.address_hash AS AddressHash, " +
            "i.account_name AS AccountName, i.state AS State, i.invited_by AS InvitedBy, i.created_at AS CreatedAt, " +
            "i.resend_count AS ResendCount, i.revoked_by AS RevokedBy, i.revoked_at AS RevokedAt, " +
            "i.accepted_member_id AS AcceptedMemberId, i.accepted_at AS AcceptedAt, ol.issued_at AS OpenLinkIssuedAt, " +
            "ol.expires_at AS OpenLinkExpiresAt, i.version AS Version, " +
            "CASE WHEN i.state = 'pending' THEN (CASE WHEN ol.issued_at IS NOT NULL AND ol.expires_at <= @now THEN 'expired' ELSE 'pending' END) " +
            "ELSE i.state END AS DisplayState, " +
            "ARRAY(SELECT t.name FROM socalytics.member_invitation_team_roles r JOIN socalytics.team t ON t.id = r.team_id " +
            "WHERE r.invitation_id = i.id ORDER BY t.name, t.id) AS TeamNames, " +
            "inviter.account_name AS InvitedByAccountName, " +
            "(SELECT CASE m.state WHEN 'queued' THEN 'queued' WHEN 'sending' THEN 'queued' WHEN 'sent' THEN 'sent' ELSE 'failed' END " +
            "FROM socalytics.member_invitation_links nl JOIN socalytics.email_messages m ON m.invitation_ref = nl.id " +
            "WHERE nl.invitation_id = i.id ORDER BY nl.created_at DESC, nl.id DESC, m.created_at DESC, m.id DESC LIMIT 1) AS DeliveryState " +
            "FROM socalytics.member_invitations i " +
            "JOIN socalytics.member_account inviter ON inviter.id = i.invited_by " +
            "LEFT JOIN LATERAL (SELECT l.issued_at, l.expires_at FROM socalytics.member_invitation_links l " +
            "WHERE l.invitation_id = i.id AND l.state IN ('pending', 'issued') ORDER BY l.created_at DESC, l.id DESC LIMIT 1) ol ON true" +
            ") x WHERE @displayState::text IS NULL OR x.DisplayState = @displayState::text ORDER BY x.CreatedAt DESC, x.Id DESC",
            new { now, displayState = displayState?.ToWireValue() }, session.Transaction, cancellationToken: cancellationToken));

        return rows
            .Select(r => new InvitationListItem(ToInvitation(r), r.TeamNames.Cast<string>().ToList(), r.InvitedByAccountName, r.DeliveryState))
            .ToList();
    }

    public Task CreatePendingLinkAsync(Guid id, Guid invitationId, DateTimeOffset createdAt, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "INSERT INTO socalytics.member_invitation_links (id, invitation_id, state, created_at) VALUES (@id, @invitationId, 'pending', @createdAt)",
            new { id, invitationId, createdAt }, cancellationToken);

    public Task<int> SupersedeOpenLinksAsync(Guid invitationId, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE socalytics.member_invitation_links SET state = 'superseded' WHERE invitation_id = @invitationId AND state IN ('pending', 'issued')",
            new { invitationId }, cancellationToken);

    public async Task<IReadOnlyList<DateTimeOffset>> ListResendLinkTimesSinceAsync(
        Guid invitationId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var times = await connection.QueryAsync<DateTime>(new CommandDefinition(
            "SELECT created_at FROM socalytics.member_invitation_links WHERE invitation_id = @invitationId AND created_at >= @since " +
            "AND id <> (SELECT id FROM socalytics.member_invitation_links WHERE invitation_id = @invitationId ORDER BY created_at, id LIMIT 1) " +
            "ORDER BY created_at, id",
            new { invitationId, since }, session.Transaction, cancellationToken: cancellationToken));
        return times.Select(EmailRows.Utc).ToList();
    }

    public Task<VersionedWriteResult> MarkResentAsync(Guid id, long expectedVersion, CancellationToken cancellationToken) =>
        Guarded(
            "UPDATE socalytics.member_invitations SET resend_count = resend_count + 1 " +
            "WHERE id = @id AND version = @expectedVersion AND state = 'pending' RETURNING version",
            new { id, expectedVersion }, id, cancellationToken);

    public async Task<int> CountLinksCreatedSinceAsync(Guid invitationId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM socalytics.member_invitation_links WHERE invitation_id = @invitationId AND created_at >= @since",
            new { invitationId, since }, session.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<StoredInvitationLink?> FindLinkAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<LinkRow>(new CommandDefinition(
            $"SELECT {LinkColumns} FROM socalytics.member_invitation_links WHERE id = @id",
            new { id }, session.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToLink(row);
    }

    public async Task<StoredInvitationLinkWithInvitation?> FindLinkByTokenHashForUpdateAsync(
        byte[] tokenHash, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);

        // Lock the invitation before the link, the order resend and revoke use, so concurrent callers cannot deadlock.
        var invitationId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "SELECT invitation_id FROM socalytics.member_invitation_links WHERE token_hash = @tokenHash",
            new { tokenHash }, transaction, cancellationToken: cancellationToken));
        if (invitationId is not { } id)
        {
            return null;
        }

        var invitation = await FindForUpdateAsync(id, cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<LinkRow>(new CommandDefinition(
            $"SELECT {LinkColumns} FROM socalytics.member_invitation_links WHERE token_hash = @tokenHash FOR UPDATE",
            new { tokenHash }, transaction, cancellationToken: cancellationToken));
        return invitation is null || row is null ? null : new StoredInvitationLinkWithInvitation(ToLink(row), invitation);
    }

    public async Task<bool> IssueLinkAsync(
        Guid id, byte[] tokenHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.member_invitation_links SET state = 'issued', token_hash = @tokenHash, issued_at = @issuedAt, expires_at = @expiresAt " +
            "WHERE id = @id AND state IN ('pending', 'issued')",
            new { id, tokenHash, issuedAt, expiresAt }, cancellationToken) > 0;

    public async Task<bool> MarkLinkUsedAsync(Guid id, DateTimeOffset usedAt, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            "UPDATE socalytics.member_invitation_links SET state = 'used', used_at = @usedAt WHERE id = @id AND state = 'issued'",
            new { id, usedAt }, cancellationToken) > 0;

    public Task<VersionedWriteResult> MarkRevokedAsync(
        Guid id, Guid revokedBy, DateTimeOffset revokedAt, long expectedVersion, CancellationToken cancellationToken) =>
        Guarded(
            "UPDATE socalytics.member_invitations SET state = 'revoked', revoked_by = @revokedBy, revoked_at = @revokedAt " +
            "WHERE id = @id AND version = @expectedVersion AND state = 'pending' RETURNING version",
            new { id, revokedBy, revokedAt, expectedVersion }, id, cancellationToken);

    public Task<VersionedWriteResult> MarkAcceptedAsync(
        Guid id, Guid memberId, DateTimeOffset acceptedAt, long expectedVersion, CancellationToken cancellationToken) =>
        Guarded(
            "UPDATE socalytics.member_invitations SET state = 'accepted', accepted_member_id = @memberId, accepted_at = @acceptedAt " +
            "WHERE id = @id AND version = @expectedVersion AND state = 'pending' RETURNING version",
            new { id, memberId, acceptedAt, expectedVersion }, id, cancellationToken);

    private Task<VersionedWriteResult> Guarded(string sql, object parameters, Guid id, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        return VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken),
            new CommandDefinition(
                "SELECT version FROM socalytics.member_invitations WHERE id = @id",
                new { id }, transaction, cancellationToken: cancellationToken));
    }

    private async Task<int> ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
    }

    private async Task<StoredMemberInvitation> ToStoredAsync(InvitationRow row, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var roles = await connection.QueryAsync<(Guid TeamId, string Role)>(new CommandDefinition(
            "SELECT team_id AS TeamId, role AS Role FROM socalytics.member_invitation_team_roles WHERE invitation_id = @id ORDER BY team_id",
            new { id = row.Id }, session.Transaction, cancellationToken: cancellationToken));
        var teamRoles = roles
            .Select(r => TeamRoleRules.TryParse(r.Role, out var role)
                ? new InvitationTeamRoleRecord(r.TeamId, role)
                : throw new InvalidOperationException($"Unknown stored team role '{r.Role}'."))
            .ToList();
        return new StoredMemberInvitation(ToInvitation(row), row.AddressNormalized, row.AddressHash, teamRoles);
    }

    private static MemberInvitation ToInvitation(InvitationRow row)
    {
        if (!AccountName.TryCreate(row.AccountName, out var accountName, out var error))
        {
            throw new InvalidOperationException($"Stored invitation account name is invalid ({error}).");
        }

        if (!InvitationStateRules.TryParse(row.State, out InvitationState state))
        {
            throw new InvalidOperationException($"Unknown stored invitation state '{row.State}'.");
        }

        return new MemberInvitation(
            row.Id, row.Address, accountName, state, row.InvitedBy, EmailRows.Utc(row.CreatedAt), row.ResendCount,
            row.RevokedBy, EmailRows.Utc(row.RevokedAt), row.AcceptedMemberId, EmailRows.Utc(row.AcceptedAt),
            EmailRows.Utc(row.OpenLinkIssuedAt), EmailRows.Utc(row.OpenLinkExpiresAt), row.Version);
    }

    private static MemberInvitation ToInvitation(ListRow row) => ToInvitation(row.ToInvitationRow());

    private static StoredInvitationLink ToLink(LinkRow row) =>
        new(row.Id, row.InvitationId, row.State, row.TokenHash, EmailRows.Utc(row.CreatedAt),
            EmailRows.Utc(row.IssuedAt), EmailRows.Utc(row.ExpiresAt), EmailRows.Utc(row.UsedAt));

    private sealed record InvitationRow(
        Guid Id, string Address, string AddressNormalized, string AddressHash, string AccountName, string State,
        Guid InvitedBy, DateTime CreatedAt, int ResendCount, Guid? RevokedBy, DateTime? RevokedAt, Guid? AcceptedMemberId,
        DateTime? AcceptedAt, DateTime? OpenLinkIssuedAt, DateTime? OpenLinkExpiresAt, long Version);

    private sealed record ListRow(
        Guid Id, string Address, string AddressNormalized, string AddressHash, string AccountName, string State,
        Guid InvitedBy, DateTime CreatedAt, int ResendCount, Guid? RevokedBy, DateTime? RevokedAt, Guid? AcceptedMemberId,
        DateTime? AcceptedAt, DateTime? OpenLinkIssuedAt, DateTime? OpenLinkExpiresAt, long Version, string DisplayState,
        Array TeamNames, string InvitedByAccountName, string? DeliveryState)
    {
        public InvitationRow ToInvitationRow() =>
            new(Id, Address, AddressNormalized, AddressHash, AccountName, State, InvitedBy, CreatedAt, ResendCount,
                RevokedBy, RevokedAt, AcceptedMemberId, AcceptedAt, OpenLinkIssuedAt, OpenLinkExpiresAt, Version);
    }

    private sealed record LinkRow(
        Guid Id, Guid InvitationId, string State, byte[]? TokenHash, DateTime CreatedAt, DateTime? IssuedAt,
        DateTime? ExpiresAt, DateTime? UsedAt);
}
