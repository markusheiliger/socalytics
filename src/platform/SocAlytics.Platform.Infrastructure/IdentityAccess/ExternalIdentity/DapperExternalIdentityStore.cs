using Dapper;
using Npgsql;
using SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;
using SocAlytics.Platform.Domain.IdentityAccess;
using SocAlytics.Platform.Infrastructure.Persistence;

namespace SocAlytics.Platform.Infrastructure.IdentityAccess.ExternalIdentity;

internal sealed class DapperExternalIdentityStore(IDbSession session) : IExternalIdentityStore
{
    private const string Columns =
        "id AS Id, account_id AS AccountId, provider_id AS ProviderId, issuer AS Issuer, subject AS Subject, " +
        "linked_at AS LinkedAt, linked_by AS LinkedBy, version AS Version";

    public async Task<ExternalIdentityLink?> FindByIssuerAndSubjectAsync(string issuer, string subject, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<LinkRow>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.external_identity_link WHERE issuer = @issuer AND subject = @subject",
            new { issuer, subject },
            session.Transaction,
            cancellationToken: cancellationToken));
        return row?.ToLink();
    }

    public async Task<ExternalIdentityInsertOutcome> InsertAsync(ExternalIdentityLink link, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("SAVEPOINT external_identity_insert", transaction: transaction, cancellationToken: cancellationToken));
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO socalytics.external_identity_link (id, account_id, provider_id, issuer, subject, linked_at, linked_by) " +
                "VALUES (@Id, @AccountId, @ProviderId, @Issuer, @Subject, @LinkedAt, @LinkedBy)",
                new { link.Id, link.AccountId, link.ProviderId, link.Issuer, link.Subject, LinkedAt = link.LinkedAt.UtcDateTime, link.LinkedBy },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition("RELEASE SAVEPOINT external_identity_insert", transaction: transaction, cancellationToken: cancellationToken));
            return ExternalIdentityInsertOutcome.Inserted;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await connection.ExecuteAsync(new CommandDefinition("ROLLBACK TO SAVEPOINT external_identity_insert", transaction: transaction, cancellationToken: cancellationToken));
            return ex.ConstraintName == "uq_external_identity_link_account_provider"
                ? ExternalIdentityInsertOutcome.AccountProviderAlreadyLinked
                : ExternalIdentityInsertOutcome.IdentityAlreadyLinked;
        }
    }

    public async Task<bool> DeleteAsync(Guid linkId, long expectedVersion, CancellationToken cancellationToken)
    {
        var transaction = session.RequireTransaction();
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM socalytics.external_identity_link WHERE id = @linkId AND version = @expectedVersion",
            new { linkId, expectedVersion },
            transaction,
            cancellationToken: cancellationToken));
        return rows > 0;
    }

    public async Task<IReadOnlyList<ExternalIdentityLink>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var connection = await session.GetConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<LinkRow>(new CommandDefinition(
            $"SELECT {Columns} FROM socalytics.external_identity_link WHERE account_id = @accountId ORDER BY linked_at, id",
            new { accountId },
            session.Transaction,
            cancellationToken: cancellationToken));
        return rows.Select(r => r.ToLink()).ToList();
    }

    private sealed class LinkRow
    {
        public Guid Id { get; set; }

        public Guid AccountId { get; set; }

        public string ProviderId { get; set; } = string.Empty;

        public string Issuer { get; set; } = string.Empty;

        public string Subject { get; set; } = string.Empty;

        public DateTimeOffset LinkedAt { get; set; }

        public Guid LinkedBy { get; set; }

        public long Version { get; set; }

        public ExternalIdentityLink ToLink() => new(Id, AccountId, ProviderId, Issuer, Subject, LinkedAt, LinkedBy, Version);
    }
}
