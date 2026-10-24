using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess.Invitations;
using SocAlytics.Platform.Domain.IdentityAccess;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Structure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class MemberInvitationStoreTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        return db;
    }

    private static async Task<int> ExecuteAsync(IsolatedDatabase db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> ScalarAsync<T>(IsolatedDatabase db, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var value2 = await command.ExecuteScalarAsync(ct);
        return value2 is null or DBNull ? default : (T)value2;
    }

    private static async Task<Guid> SeedTeamAsync(IsolatedDatabase db, string name, CancellationToken ct)
    {
        var season = Guid.NewGuid();
        var team = Guid.NewGuid();
        await ExecuteAsync(db, "INSERT INTO socalytics.season (id, name, created_at) VALUES (@id, 'S', @now)", ct, ("id", season), ("now", Now));
        await ExecuteAsync(
            db, "INSERT INTO socalytics.team (id, season_id, name, created_at) VALUES (@id, @season, @name, @now)", ct,
            ("id", team), ("season", season), ("name", name), ("now", Now));
        return team;
    }

    private static Task InsertInvitationAsync(
        IsolatedDatabase db, Guid id, Guid invitedBy, string accountName, string address, string state, CancellationToken ct) =>
        ExecuteAsync(
            db,
            "INSERT INTO socalytics.member_invitations (id, address, address_normalized, address_hash, account_name, account_name_normalized, " +
            "state, invited_by, created_at, revoked_by, revoked_at) " +
            "VALUES (@id, @address, @normalized, 'hash', @name, @nameNormalized, @state, @by, @now, @revokedBy, @revokedAt)",
            ct,
            ("id", id), ("address", address), ("normalized", address.ToUpperInvariant()), ("name", accountName),
            ("nameNormalized", accountName.ToUpperInvariant()), ("state", state), ("by", invitedBy), ("now", Now),
            ("revokedBy", state == "revoked" ? invitedBy : DBNull.Value), ("revokedAt", state == "revoked" ? Now : DBNull.Value));

    private static Task InsertLinkAsync(
        IsolatedDatabase db, Guid id, Guid invitationId, string state, byte[]? tokenHash, bool issued, DateTimeOffset? usedAt, CancellationToken ct) =>
        ExecuteAsync(
            db,
            "INSERT INTO socalytics.member_invitation_links (id, invitation_id, state, token_hash, created_at, issued_at, expires_at, used_at) " +
            "VALUES (@id, @invitation, @state, @token, @now, @issued, @expires, @used)",
            ct,
            ("id", id), ("invitation", invitationId), ("state", state), ("token", (object?)tokenHash ?? DBNull.Value), ("now", Now),
            ("issued", issued ? Now : DBNull.Value), ("expires", issued ? Now.AddDays(7) : DBNull.Value),
            ("used", (object?)usedAt ?? DBNull.Value));

    private static Task InsertMessageAsync(
        IsolatedDatabase db, Guid id, string type, Guid? accountId, Guid? invitationRef, string state, DateTimeOffset createdAt, CancellationToken ct) =>
        ExecuteAsync(
            db,
            "INSERT INTO socalytics.email_messages (id, notification_type, account_id, address_hash, invitation_ref, state, next_attempt_at, created_at) " +
            "VALUES (@id, @type, @account, 'hash', @ref, @state, @at, @at)",
            ct,
            ("id", id), ("type", type), ("account", (object?)accountId ?? DBNull.Value), ("ref", (object?)invitationRef ?? DBNull.Value),
            ("state", state), ("at", createdAt));

    private static async Task<T> InUnitOfWorkAsync<T>(ServiceProvider provider, Func<IMemberInvitationStore, Task<T>> action, CancellationToken ct)
    {
        await using var scope = provider.CreateAsyncScope();
        await using var work = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        var result = await action(scope.ServiceProvider.GetRequiredService<IMemberInvitationStore>());
        await work.CommitAsync(ct);
        return result;
    }

    private static NewMemberInvitation NewInvitation(
        Guid id, Guid invitedBy, string accountName, string address, params InvitationTeamRoleRecord[] roles)
    {
        AccountName.TryCreate(accountName, out var name, out _).ShouldBeTrue();
        return new NewMemberInvitation(id, address, address.ToUpperInvariant(), "hash", name!, invitedBy, Now, roles);
    }

    [Fact]
    public async Task MigrationAppliesAndTheStructureTestsAcceptTheTablesAndColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);

        foreach (var table in new[] { "member_invitations", "member_invitation_team_roles", "member_invitation_links" })
        {
            (await ScalarAsync<long>(
                db, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'socalytics' AND table_name = @t", ct, ("t", table)))
                .ShouldBe(1, table);
        }

        (await ScalarAsync<long>(
            db, "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'socalytics' AND table_name = 'email_messages' " +
            "AND column_name = 'invitation_ref'", ct)).ShouldBe(1);
        (await ScalarAsync<string>(
            db, "SELECT is_nullable FROM information_schema.columns WHERE table_schema = 'socalytics' AND table_name = 'email_messages' " +
            "AND column_name = 'account_id'", ct)).ShouldBe("YES");

        var violations = await PersistenceStructureChecker.CheckAsync(
            db.MigratorConnectionString, PersistedTableClassifications.Platform, ct);
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public async Task TheVersionTriggerBumpsTheInvitationOnItsOwnAndItsTeamRolesButNotOnLinkChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var admin = await TestMembers.SeedAsync(db, "version.admin", cancellationToken: ct);
        var team = await SeedTeamAsync(db, "Version team", ct);
        var invitation = Guid.NewGuid();
        await InsertInvitationAsync(db, invitation, admin, "version.invitee", "version@example.com", "pending", ct);

        async Task<long> VersionAsync() =>
            (await ScalarAsync<long>(db, "SELECT version FROM socalytics.member_invitations WHERE id = @id", ct, ("id", invitation)));

        var version = await VersionAsync();
        version.ShouldBe(1);

        await ExecuteAsync(db, "UPDATE socalytics.member_invitations SET resend_count = resend_count + 1 WHERE id = @id", ct, ("id", invitation));
        (await VersionAsync()).ShouldBe(version + 1);
        version++;

        await ExecuteAsync(
            db, "INSERT INTO socalytics.member_invitation_team_roles (invitation_id, team_id, role) VALUES (@i, @t, 'coach')", ct,
            ("i", invitation), ("t", team));
        (await VersionAsync()).ShouldBe(version + 1);
        version++;

        await ExecuteAsync(
            db, "UPDATE socalytics.member_invitation_team_roles SET role = 'viewer' WHERE invitation_id = @i", ct, ("i", invitation));
        (await VersionAsync()).ShouldBe(version + 1);
        version++;

        var link = Guid.NewGuid();
        await InsertLinkAsync(db, link, invitation, "pending", null, issued: false, usedAt: null, ct);
        await ExecuteAsync(
            db,
            "UPDATE socalytics.member_invitation_links SET state = 'issued', token_hash = '\\x010203', issued_at = @now, expires_at = @later WHERE id = @id",
            ct, ("id", link), ("now", Now), ("later", Now.AddDays(7)));
        await ExecuteAsync(db, "UPDATE socalytics.member_invitation_links SET state = 'superseded' WHERE id = @id", ct, ("id", link));
        (await VersionAsync()).ShouldBe(version);
    }

    [Fact]
    public async Task PendingUniqueIndexesRefuseASecondPendingInvitationAndAcceptOneAfterRevocation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var admin = await TestMembers.SeedAsync(db, "unique.admin", cancellationToken: ct);
        var first = Guid.NewGuid();
        await InsertInvitationAsync(db, first, admin, "unique.invitee", "unique@example.com", "pending", ct);

        var sameName = await Should.ThrowAsync<PostgresException>(() =>
            InsertInvitationAsync(db, Guid.NewGuid(), admin, "unique.invitee", "other@example.com", "pending", ct));
        sameName.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        var sameAddress = await Should.ThrowAsync<PostgresException>(() =>
            InsertInvitationAsync(db, Guid.NewGuid(), admin, "other.invitee", "unique@example.com", "pending", ct));
        sameAddress.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);

        await ExecuteAsync(
            db, "UPDATE socalytics.member_invitations SET state = 'revoked', revoked_by = @by, revoked_at = @now WHERE id = @id", ct,
            ("id", first), ("by", admin), ("now", Now));

        await InsertInvitationAsync(db, Guid.NewGuid(), admin, "unique.invitee", "unique@example.com", "pending", ct);
    }

    [Fact]
    public async Task LinkConstraintsRefuseInconsistentRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var admin = await TestMembers.SeedAsync(db, "link.admin", cancellationToken: ct);
        var invitation = Guid.NewGuid();
        await InsertInvitationAsync(db, invitation, admin, "link.invitee", "link@example.com", "pending", ct);
        var token = new byte[] { 1, 2, 3 };

        await Should.ThrowAsync<PostgresException>(() =>
            InsertLinkAsync(db, Guid.NewGuid(), invitation, "pending", token, issued: false, usedAt: null, ct));
        await Should.ThrowAsync<PostgresException>(() =>
            InsertLinkAsync(db, Guid.NewGuid(), invitation, "used", token, issued: true, usedAt: null, ct));

        await InsertLinkAsync(db, Guid.NewGuid(), invitation, "pending", null, issued: false, usedAt: null, ct);
        await InsertLinkAsync(db, Guid.NewGuid(), invitation, "used", token, issued: true, usedAt: Now, ct);

        var duplicate = await Should.ThrowAsync<PostgresException>(() =>
            InsertLinkAsync(db, Guid.NewGuid(), invitation, "issued", token, issued: true, usedAt: null, ct));
        duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task EmailMessageCheckTiesTheInvitationTypeToALinkWithoutAnAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var admin = await TestMembers.SeedAsync(db, "message.admin", cancellationToken: ct);
        var invitation = Guid.NewGuid();
        await InsertInvitationAsync(db, invitation, admin, "message.invitee", "message@example.com", "pending", ct);
        var link = Guid.NewGuid();
        await InsertLinkAsync(db, link, invitation, "pending", null, issued: false, usedAt: null, ct);

        var withAccount = await Should.ThrowAsync<PostgresException>(() =>
            InsertMessageAsync(db, Guid.NewGuid(), "member-invitation", admin, link, "queued", Now, ct));
        withAccount.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        await Should.ThrowAsync<PostgresException>(() =>
            InsertMessageAsync(db, Guid.NewGuid(), "member-invitation", null, null, "queued", Now, ct));
        await Should.ThrowAsync<PostgresException>(() =>
            InsertMessageAsync(db, Guid.NewGuid(), "email-confirmation", null, link, "queued", Now, ct));
        await Should.ThrowAsync<PostgresException>(() =>
            InsertMessageAsync(db, Guid.NewGuid(), "email-confirmation", null, null, "queued", Now, ct));

        await InsertMessageAsync(db, Guid.NewGuid(), "member-invitation", null, link, "queued", Now, ct);
        await InsertMessageAsync(db, Guid.NewGuid(), "email-confirmation", admin, null, "queued", Now, ct);
    }

    [Fact]
    public async Task CreatingALinkSupersedesTheOpenOnesAndLeavesFinalOnesAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var admin = await TestMembers.SeedAsync(db, "supersede.admin", cancellationToken: ct);
        var team = await SeedTeamAsync(db, "Supersede team", ct);
        await using var provider = PlatformServices.Build(db);
        var invitation = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        await InUnitOfWorkAsync(provider, async store =>
        {
            (await store.InsertAsync(
                NewInvitation(invitation, admin, "supersede.invitee", "supersede@example.com", new InvitationTeamRoleRecord(team, TeamRole.Coach)), ct))
                .ShouldBeTrue();
            await store.CreatePendingLinkAsync(first, invitation, Now, ct);
            return true;
        }, ct);
        await InUnitOfWorkAsync(provider, async store =>
            await store.IssueLinkAsync(first, [1], Now, Now.AddDays(7), ct), ct);

        var superseded = await InUnitOfWorkAsync(provider, async store =>
        {
            var changed = await store.SupersedeOpenLinksAsync(invitation, ct);
            await store.CreatePendingLinkAsync(second, invitation, Now.AddMinutes(1), ct);
            return changed;
        }, ct);
        superseded.ShouldBe(1);

        var supersededAgain = await InUnitOfWorkAsync(provider, async store =>
        {
            var changed = await store.SupersedeOpenLinksAsync(invitation, ct);
            await store.CreatePendingLinkAsync(third, invitation, Now.AddMinutes(2), ct);
            return changed;
        }, ct);
        supersededAgain.ShouldBe(1);

        async Task<string?> StateAsync(Guid id) =>
            await ScalarAsync<string>(db, "SELECT state FROM socalytics.member_invitation_links WHERE id = @id", ct, ("id", id));
        (await StateAsync(first)).ShouldBe("superseded");
        (await StateAsync(second)).ShouldBe("superseded");
        (await StateAsync(third)).ShouldBe("pending");

        (await InUnitOfWorkAsync(provider, store => store.CountLinksCreatedSinceAsync(invitation, Now.AddMinutes(1), ct), ct)).ShouldBe(2);
    }

    [Fact]
    public async Task TheListDerivesExpiredFromTheNewestLinkAndReportsTheDeliveryState()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var admin = await TestMembers.SeedAsync(db, "list.admin", cancellationToken: ct);
        var teamA = await SeedTeamAsync(db, "Alpha", ct);
        var teamB = await SeedTeamAsync(db, "Beta", ct);
        await using var provider = PlatformServices.Build(db);

        var expired = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        var noMessage = Guid.NewGuid();
        var expiredLink = Guid.NewGuid();
        var pendingLink = Guid.NewGuid();
        var revokedLink = Guid.NewGuid();

        await InUnitOfWorkAsync(provider, async store =>
        {
            (await store.InsertAsync(NewInvitation(
                expired, admin, "list.expired", "expired@example.com",
                new InvitationTeamRoleRecord(teamB, TeamRole.Viewer), new InvitationTeamRoleRecord(teamA, TeamRole.Coach)), ct)).ShouldBeTrue();
            (await store.InsertAsync(NewInvitation(pending, admin, "list.pending", "pending@example.com"), ct)).ShouldBeTrue();
            (await store.InsertAsync(NewInvitation(revoked, admin, "list.revoked", "revoked@example.com"), ct)).ShouldBeTrue();
            (await store.InsertAsync(NewInvitation(noMessage, admin, "list.nomessage", "nomessage@example.com"), ct)).ShouldBeTrue();
            await store.CreatePendingLinkAsync(expiredLink, expired, Now, ct);
            await store.CreatePendingLinkAsync(pendingLink, pending, Now, ct);
            await store.CreatePendingLinkAsync(revokedLink, revoked, Now, ct);
            return true;
        }, ct);
        await InUnitOfWorkAsync(provider, async store =>
        {
            (await store.IssueLinkAsync(expiredLink, [1], Now.AddDays(-8), Now.AddDays(-1), ct)).ShouldBeTrue();
            (await store.IssueLinkAsync(pendingLink, [2], Now, Now.AddDays(7), ct)).ShouldBeTrue();
            await store.SupersedeOpenLinksAsync(revoked, ct);
            (await store.MarkRevokedAsync(revoked, admin, Now, 1, ct)).Outcome.ShouldBe(VersionedWriteOutcome.Applied);
            return true;
        }, ct);

        await InsertMessageAsync(db, Guid.NewGuid(), "member-invitation", null, expiredLink, "sending", Now.AddMinutes(1), ct);
        await InsertMessageAsync(db, Guid.NewGuid(), "member-invitation", null, pendingLink, "failed", Now.AddMinutes(1), ct);
        await InsertMessageAsync(db, Guid.NewGuid(), "member-invitation", null, pendingLink, "sent", Now.AddMinutes(2), ct);
        await InsertMessageAsync(db, Guid.NewGuid(), "member-invitation", null, revokedLink, "discarded", Now.AddMinutes(1), ct);

        var all = await InUnitOfWorkAsync(provider, store => store.ListAsync(null, Now, ct), ct);
        all.Count.ShouldBe(4);

        InvitationListItem Item(Guid id) => all.Single(i => i.Invitation.Id == id);
        Item(expired).Invitation.DisplayState(Now).ShouldBe(InvitationDisplayState.Expired);
        Item(expired).DeliveryState.ShouldBe("queued");
        Item(expired).TeamNames.ShouldBe(["Alpha", "Beta"]);
        Item(expired).InvitedByAccountName.ShouldBe("list.admin");
        Item(pending).Invitation.DisplayState(Now).ShouldBe(InvitationDisplayState.Pending);
        Item(pending).DeliveryState.ShouldBe("sent");
        Item(revoked).Invitation.DisplayState(Now).ShouldBe(InvitationDisplayState.Revoked);
        Item(revoked).DeliveryState.ShouldBe("failed");
        Item(noMessage).DeliveryState.ShouldBeNull();

        var expiredOnly = await InUnitOfWorkAsync(provider, store => store.ListAsync(InvitationDisplayState.Expired, Now, ct), ct);
        expiredOnly.Select(i => i.Invitation.Id).ShouldBe([expired]);
        var pendingOnly = await InUnitOfWorkAsync(provider, store => store.ListAsync(InvitationDisplayState.Pending, Now, ct), ct);
        pendingOnly.Select(i => i.Invitation.Id).ToHashSet().ShouldBe([pending, noMessage], ignoreOrder: true);
    }
}
