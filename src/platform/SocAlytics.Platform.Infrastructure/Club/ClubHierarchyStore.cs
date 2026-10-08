using Dapper;
using Npgsql;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Club;
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

    private sealed record ClubRow(Guid Id, string DisplayName, Guid BootstrapAdminAccountId, DateTime CreatedAt, long Version);
}
