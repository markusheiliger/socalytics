using Dapper;
using Npgsql;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Club;
using SocAlytics.Platform.Infrastructure.Persistence;
using ClubEntity = SocAlytics.Platform.Domain.Club.Club;

namespace SocAlytics.Platform.Infrastructure.Club;

internal sealed class ClubHierarchyStore(IDbSession session) : IClubHierarchyStore
{
    public async Task LockBootstrapAsync(CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@key)",
            new { key = AdvisoryLockKeys.ClubBootstrap },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<ClubEntity?> GetClubAsync(CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ClubRow>(new CommandDefinition(
            "SELECT id AS Id, display_name AS DisplayName, bootstrap_admin_account_id AS BootstrapAdminAccountId, " +
            "created_at AS CreatedAt, version AS Version FROM socalytics.club",
            transaction: session.Transaction,
            cancellationToken: cancellationToken));
        if (row is null || !DisplayName.TryCreate(row.DisplayName, out var displayName, out _))
        {
            return null;
        }

        return new ClubEntity(row.Id, displayName, row.BootstrapAdminAccountId, new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)), row.Version);
    }

    public async Task InsertClubAsync(ClubEntity club, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO socalytics.club (id, display_name, bootstrap_admin_account_id, created_at) " +
                "VALUES (@Id, @DisplayName, @BootstrapAdminAccountId, @CreatedAt)",
                new { club.Id, DisplayName = club.DisplayName.Value, club.BootstrapAdminAccountId, club.CreatedAt },
                transaction,
                cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new UniqueViolationException(ex.ConstraintName ?? "club", ex);
        }
    }

    public async Task<VersionedWriteResult> UpdateClubDisplayNameAsync(DisplayName name, long expectedVersion, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        return await VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(
                "UPDATE socalytics.club SET display_name = @Name WHERE id = (SELECT id FROM socalytics.club) AND version = @ExpectedVersion RETURNING version",
                new { Name = name.Value, ExpectedVersion = expectedVersion },
                transaction,
                cancellationToken: cancellationToken),
            new CommandDefinition(
                "SELECT version FROM socalytics.club",
                transaction: transaction,
                cancellationToken: cancellationToken));
    }

    public async Task InsertSeasonAsync(Season season, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.season (id, name, state, created_at) VALUES (@Id, @Name, @State, @CreatedAt)",
            new { season.Id, Name = season.Name.Value, State = season.State.ToWire(), season.CreatedAt },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<Season?> GetSeasonAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<SeasonRow>(new CommandDefinition(
            SeasonSelect + " WHERE id = @id",
            new { id },
            session.Transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : ToSeason(row);
    }

    public async Task<SeasonPageResult> ListSeasonsAsync(SeasonPageKey? after, int pageSize, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<SeasonRow>(new CommandDefinition(
            SeasonSelect + " WHERE @hasAfter = false OR (created_at, id) > (@afterAt, @afterId) ORDER BY created_at, id LIMIT @limit",
            new
            {
                hasAfter = after is not null,
                afterAt = after?.CreatedAt.UtcDateTime ?? DateTime.UnixEpoch,
                afterId = after?.Id ?? Guid.Empty,
                limit = pageSize + 1,
            },
            session.Transaction,
            cancellationToken: cancellationToken))).ToList();

        var items = rows.Take(pageSize).Select(ToSeason).ToList();
        var last = items.Count > 0 ? new SeasonPageKey(items[^1].CreatedAt, items[^1].Id) : null;
        return new SeasonPageResult(items, rows.Count > pageSize, last);
    }

    public async Task<VersionedWriteResult> TransitionSeasonAsync(Guid id, SeasonState expectedState, SeasonState newState, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var stamp = newState == SeasonState.Active ? "activated_at = @At" : "archived_at = @At";
        try
        {
            return await VersionedWrites.ExecuteAsync(
                session,
                new CommandDefinition(
                    $"UPDATE socalytics.season SET state = @NewState, {stamp} WHERE id = @Id AND state = @ExpectedState RETURNING version",
                    new { Id = id, ExpectedState = expectedState.ToWire(), NewState = newState.ToWire(), At = at.UtcDateTime },
                    transaction,
                    cancellationToken: cancellationToken),
                new CommandDefinition(
                    "SELECT version FROM socalytics.season WHERE id = @Id",
                    new { Id = id },
                    transaction,
                    cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new UniqueViolationException(ex.ConstraintName ?? "ux_season_single_active", ex);
        }
    }

    public async Task<SeasonState?> LockSeasonForShareAsync(Guid seasonId, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var state = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT state FROM socalytics.season WHERE id = @seasonId FOR SHARE",
            new { seasonId },
            transaction,
            cancellationToken: cancellationToken));
        return SeasonStateWire.TryParse(state, out var parsed) ? parsed : null;
    }

    public async Task InsertTeamAsync(Team team, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO socalytics.team (id, season_id, name, created_at) VALUES (@Id, @SeasonId, @Name, @CreatedAt)",
            new { team.Id, team.SeasonId, Name = team.Name.Value, team.CreatedAt },
            transaction,
            cancellationToken: cancellationToken));
    }

    public async Task<TeamView?> GetTeamAsync(Guid id, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TeamRow>(new CommandDefinition(
            TeamSelect + " WHERE t.id = @id",
            new { id },
            session.Transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : ToTeamView(row);
    }

    public async Task<TeamPageResult> ListTeamsAsync(Guid? seasonId, TeamVisibility visibility, TeamPageKey? after, int pageSize, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<TeamRow>(new CommandDefinition(
            TeamSelect +
            " WHERE (@hasSeason = false OR t.season_id = @seasonId) AND (@allTeams = true OR t.id = ANY(@ids))" +
            " AND (@hasAfter = false OR (t.name, t.id) > (@afterName, @afterId)) ORDER BY t.name, t.id LIMIT @limit",
            new
            {
                hasSeason = seasonId is not null,
                seasonId = seasonId ?? Guid.Empty,
                allTeams = visibility.AllTeams,
                ids = visibility.TeamIds.ToArray(),
                hasAfter = after is not null,
                afterName = after?.Name ?? string.Empty,
                afterId = after?.Id ?? Guid.Empty,
                limit = pageSize + 1,
            },
            session.Transaction,
            cancellationToken: cancellationToken))).ToList();

        var items = rows.Take(pageSize).Select(ToTeamView).ToList();
        var last = items.Count > 0 ? new TeamPageKey(items[^1].Team.Name.Value, items[^1].Team.Id) : null;
        return new TeamPageResult(items, rows.Count > pageSize, last);
    }

    public async Task<VersionedWriteResult> UpdateTeamNameAsync(Guid id, DisplayName name, long expectedVersion, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        return await VersionedWrites.ExecuteAsync(
            session,
            new CommandDefinition(
                "UPDATE socalytics.team SET name = @Name WHERE id = @Id AND version = @ExpectedVersion RETURNING version",
                new { Id = id, Name = name.Value, ExpectedVersion = expectedVersion },
                transaction,
                cancellationToken: cancellationToken),
            new CommandDefinition(
                "SELECT version FROM socalytics.team WHERE id = @Id",
                new { Id = id },
                transaction,
                cancellationToken: cancellationToken));
    }

    private const string TeamSelect =
        "SELECT t.id AS Id, t.season_id AS SeasonId, t.name AS Name, t.created_at AS CreatedAt, t.version AS Version, s.state AS SeasonState " +
        "FROM socalytics.team t JOIN socalytics.season s ON s.id = t.season_id";

    private static TeamView ToTeamView(TeamRow row)
    {
        DisplayName.TryCreate(row.Name, out var name, out _);
        SeasonStateWire.TryParse(row.SeasonState, out var state);
        return new TeamView(new Team(row.Id, row.SeasonId, name, Utc(row.CreatedAt), row.Version), state);
    }

    private sealed record TeamRow(Guid Id, Guid SeasonId, string Name, DateTime CreatedAt, long Version, string SeasonState);

    private const string SeasonSelect =
        "SELECT id AS Id, name AS Name, state AS State, created_at AS CreatedAt, activated_at AS ActivatedAt, " +
        "archived_at AS ArchivedAt, version AS Version FROM socalytics.season";

    private static Season ToSeason(SeasonRow row)
    {
        DisplayName.TryCreate(row.Name, out var name, out _);
        SeasonStateWire.TryParse(row.State, out var state);
        return new Season(row.Id, name, state, Utc(row.CreatedAt), row.ActivatedAt is { } a ? Utc(a) : null, row.ArchivedAt is { } r ? Utc(r) : null, row.Version);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record SeasonRow(Guid Id, string Name, string State, DateTime CreatedAt, DateTime? ActivatedAt, DateTime? ArchivedAt, long Version);

    private sealed record ClubRow(Guid Id, string DisplayName, Guid BootstrapAdminAccountId, DateTime CreatedAt, long Version);
}
